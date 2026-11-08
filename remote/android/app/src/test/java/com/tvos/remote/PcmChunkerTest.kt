package com.tvos.remote

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

class PcmChunkerTest {
    @Test
    fun OneHundredMillisecondsIsOneLittleEndianChunk() {
        val chunker = PcmChunker()
        val chunks = chunker.add(ShortArray(1600) { 0x1234 }, 1600)
        assertEquals(1, chunks.size)
        assertEquals(3200, chunks[0].size)
        assertEquals(0x34.toByte(), chunks[0][0])
        assertEquals(0x12.toByte(), chunks[0][1])
        assertEquals(0x34.toByte(), chunks[0][3198])
        assertEquals(0x12.toByte(), chunks[0][3199])
    }

    @Test
    fun TheRestWaitsForTheNextCall() {
        val chunker = PcmChunker()
        val first = chunker.add(ShortArray(2000) { it.toShort() }, 2000)
        assertEquals(1, first.size)
        // 400 samples (1600 to 1999) are kept; 1200 more make the second chunk, which starts with them.
        val second = chunker.add(ShortArray(1200) { (2000 + it).toShort() }, 1200)
        assertEquals(1, second.size)
        assertArrayEquals(littleEndian(1600 until 3200), second[0])
    }

    @Test
    fun OnlyTheSamplesReadCount() {
        val chunker = PcmChunker()
        // A read can fill less than the buffer; what is past n is old and must not be sent.
        assertEquals(0, chunker.add(ShortArray(1600) { 7 }, 1000).size)
        val chunks = chunker.add(ShortArray(1600) { 9 }, 600)
        assertEquals(1, chunks.size)
        assertEquals(7.toByte(), chunks[0][1998])
        assertEquals(9.toByte(), chunks[0][2000])
    }

    private fun littleEndian(samples: IntRange): ByteArray =
        samples.flatMap { listOf((it and 0xFF).toByte(), (it shr 8).toByte()) }.toByteArray()
}
