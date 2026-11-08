package com.tvos.remote

import org.junit.Assert.assertEquals
import org.junit.Test

class MicPumpTest {
    private val chunks = mutableListOf<ByteArray>()
    private var failures = 0
    private var reads = 0

    // Each read gives the next of [results] (samples read, or an error code), filling the buffer with 5s.
    private fun pump(vararg results: Int, running: () -> Boolean = { true }) = pumpMic(
        ShortArray(1600),
        { buf -> buf.fill(5); results[reads++] },
        running,
        { chunks.add(it) },
        { failures++ },
    )

    @Test
    fun AReadOfNothingEndsItAndSaysSo() {
        pump(1600, 0)
        assertEquals(1, chunks.size)
        assertEquals(2, reads) // no spinning on 0
        assertEquals(1, failures)
    }

    @Test
    fun AReadErrorEndsItAndSaysSo() {
        pump(-3) // AudioRecord.ERROR_INVALID_OPERATION
        assertEquals(0, chunks.size)
        assertEquals(1, failures)
    }

    @Test
    fun AStopIsNotAFailure() {
        // stop() makes the read in progress return; the reading ends without a word.
        var running = true
        pumpMic(ShortArray(1600), { running = false; 0 }, { running }, { chunks.add(it) }, { failures++ })
        assertEquals(0, chunks.size)
        assertEquals(0, failures)
    }

    @Test
    fun SoundGoesOnUntilTheStop() {
        // Running until the third read is under way: that read is the stop's, so two chunks go.
        pump(1600, 1600, 1600, running = { reads < 3 })
        assertEquals(2, chunks.size)
        assertEquals(3, reads)
        assertEquals(0, failures)
    }
}
