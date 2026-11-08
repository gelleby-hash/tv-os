package com.tvos.remote

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder

/**
 * Cuts the microphone's samples into the frames the launcher takes: [chunkBytes] of PCM16 little-endian each,
 * 3200 bytes being 100 ms of 16 kHz mono. Samples short of a whole chunk wait for the next [add].
 */
class PcmChunker(private val chunkBytes: Int = 3200) {
    init {
        require(chunkBytes > 0 && chunkBytes % 2 == 0) { "A chunk is a whole number of 16-bit samples" }
    }

    private val pending = ByteArray(chunkBytes)
    private var filled = 0

    /** Adds the first [n] samples of [buf] and returns the chunks they completed, oldest first. */
    fun add(buf: ShortArray, n: Int): List<ByteArray> {
        require(n in 0..buf.size)
        val chunks = ArrayList<ByteArray>(1)
        for (i in 0 until n) {
            val sample = buf[i].toInt()
            pending[filled++] = sample.toByte()
            pending[filled++] = (sample shr 8).toByte()
            if (filled == chunkBytes) {
                chunks.add(pending.copyOf())
                filled = 0
            }
        }
        return chunks
    }
}

/**
 * Reads the microphone with [read] into [samples] and hands [onChunk] each whole chunk of [samples]' size,
 * until [running] turns false. A read that gives nothing (0) or an error (below 0) while still running ends
 * the reading and calls [onFailed] once: the microphone has stopped giving sound, and waiting for the
 * launcher's limit would leave the button red over silence (or spin on 0). Kept apart from [MicCapture] so it
 * runs on the JVM in tests.
 */
internal fun pumpMic(
    samples: ShortArray,
    read: (ShortArray) -> Int,
    running: () -> Boolean,
    onChunk: (ByteArray) -> Unit,
    onFailed: () -> Unit,
) {
    val chunker = PcmChunker(samples.size * 2)
    while (running()) {
        val n = read(samples)
        if (!running())
            return // stopped while reading: the stop, not a failure
        if (n <= 0) {
            onFailed()
            return
        }
        chunker.add(samples, n).forEach(onChunk)
    }
}

/**
 * The phone's microphone, as the launcher wants it: 16000 Hz, mono, PCM16, in 100 ms chunks. It records only
 * between [start] and [stop], on a thread of its own, and [stop] waits for that thread, so nothing records
 * after it returns. Used from the main thread.
 */
class MicCapture {
    private var record: AudioRecord? = null
    private var reader: Thread? = null
    @Volatile private var running = false

    /**
     * Starts recording and hands [onChunk] each 100 ms chunk, on the recording thread. False if the microphone
     * would not open (no permission, or the phone refused it); then nothing records. If it stops giving sound
     * later, [onFailed] is called once, on the recording thread, and the caller is to [stop].
     */
    // The caller asks for RECORD_AUDIO before the first start; without it the constructor throws, caught here.
    @SuppressLint("MissingPermission")
    fun start(onChunk: (ByteArray) -> Unit, onFailed: () -> Unit): Boolean {
        if (record != null)
            return true
        val minimum = AudioRecord.getMinBufferSize(RATE, CHANNEL, ENCODING)
        if (minimum <= 0)
            return false
        val opened = try {
            // VOICE_RECOGNITION: the phone's tuning for speech to a recogniser, which YouTube's voice search is.
            AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, RATE, CHANNEL, ENCODING, maxOf(minimum, CHUNK_BYTES * 4))
        } catch (e: Exception) {
            return false
        }
        if (opened.state != AudioRecord.STATE_INITIALIZED) {
            opened.release()
            return false
        }
        try {
            opened.startRecording()
        } catch (e: IllegalStateException) {
            opened.release()
            return false
        }
        if (opened.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
            opened.release()
            return false
        }
        record = opened
        running = true
        reader = Thread({
            pumpMic(ShortArray(CHUNK_BYTES / 2), { opened.read(it, 0, it.size) }, { running }, onChunk, onFailed)
        }, "mic").apply { start() }
        return true
    }

    fun stop() {
        val opened = record ?: return
        running = false
        // stop() makes a read in progress return, so the thread ends within one read.
        try {
            opened.stop()
        } catch (e: IllegalStateException) {
            // Not recording any more; the release below is all that is left.
        }
        reader?.join(500)
        opened.release()
        record = null
        reader = null
    }

    private companion object {
        const val RATE = 16000
        const val CHANNEL = AudioFormat.CHANNEL_IN_MONO
        const val ENCODING = AudioFormat.ENCODING_PCM_16BIT
        const val CHUNK_BYTES = 3200
    }
}
