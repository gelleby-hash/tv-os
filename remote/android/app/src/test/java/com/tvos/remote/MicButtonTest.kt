package com.tvos.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class MicButtonTest {
    @Test
    fun GreyedUntilTheStateSaysMicAvailable() {
        val mic = MicButton()
        assertFalse(mic.look(connected = true).enabled)
        assertEquals(MicButton.Tap.Nothing, mic.tap())
        mic.onState(micAvailable = true)
        assertTrue(mic.look(connected = true).enabled)
        assertFalse(mic.look(connected = false).enabled)
    }

    @Test
    fun ListensOnlyAfterTheLauncherSaysSo() {
        val mic = listeningButton()
        assertTrue(mic.listening)
        assertEquals(MicLook(enabled = true, listening = true, line = "Listening... speak now"), mic.look(connected = true))
    }

    @Test
    fun StopStaysEnabledWhileThisPhoneListens() {
        // The launcher tells every phone, this one included, that the microphone is taken (ruling R13).
        val mic = listeningButton()
        mic.onState(micAvailable = false)
        assertTrue(mic.look(connected = true).enabled)
        assertEquals(MicButton.Tap.Stop, mic.tap())
        assertFalse(mic.listening)
        assertFalse(mic.look(connected = true).enabled) // until the launcher answers the stop
        assertFalse(mic.onMic(listening = false, reason = "stopped"))
        assertEquals(MicLook(enabled = false, listening = false, line = null), mic.look(connected = true))
    }

    @Test
    fun ALateStartAfterAStopIsIgnored() {
        val mic = MicButton()
        mic.onState(micAvailable = true)
        mic.tap()
        assertEquals(MicButton.Tap.Stop, mic.tap()) // stopped before the launcher answered the start
        assertFalse(mic.onMic(listening = true, reason = null))
        assertFalse(mic.listening)
    }

    @Test
    fun EndsGoBackByThemselves() {
        for (reason in listOf("done", "timeout", "left-app", "stopped")) {
            val mic = listeningButton()
            mic.onMic(listening = false, reason = reason)
            mic.onState(micAvailable = true)
            assertEquals(reason, MicLook(enabled = true, listening = false, line = null), mic.look(connected = true))
        }
    }

    @Test
    fun RefusalsShowAShortLine() {
        val lines = mapOf(
            "busy" to "Voice search busy, try again",
            "not-youtube" to "Open YouTube first",
            "failed" to "Voice search didn't start",
        )
        for ((reason, line) in lines) {
            val mic = MicButton()
            mic.onState(micAvailable = true)
            assertEquals(MicButton.Tap.Start, mic.tap())
            assertFalse(mic.onMic(listening = false, reason = reason))
            assertEquals(reason, line, mic.look(connected = true).line)
            // The next tap clears the line.
            mic.tap()
            assertNull(mic.look(connected = true).line)
        }
    }

    @Test
    fun ACaptureFailureStopsAndSaysSo() {
        // The phone's own microphone would not open, or stopped giving sound: the phone stops the session.
        val mic = listeningButton()
        assertEquals(MicButton.Tap.Stop, mic.captureFailed())
        assertFalse(mic.listening)
        assertEquals("Voice search didn't start", mic.look(connected = true).line)
        // The line outlives the launcher's answer to the stop.
        mic.onMic(listening = false, reason = "stopped")
        assertEquals("Voice search didn't start", mic.look(connected = true).line)
    }

    @Test
    fun ACaptureFailureAfterTheLauncherEndedIsQuiet() {
        val mic = listeningButton()
        mic.onMic(listening = false, reason = "done")
        assertEquals(MicButton.Tap.Nothing, mic.captureFailed())
        assertNull(mic.look(connected = true).line)
        assertEquals(MicButton.Tap.Nothing, MicButton().captureFailed())
    }

    @Test
    fun ADisconnectForgetsEverything() {
        val mic = listeningButton()
        mic.reset()
        assertFalse(mic.listening)
        assertEquals(MicLook(enabled = false, listening = false, line = null), mic.look(connected = true))
        assertEquals(MicButton.Tap.Nothing, mic.tap())
    }

    private fun listeningButton(): MicButton {
        val mic = MicButton()
        mic.onState(micAvailable = true)
        assertEquals(MicButton.Tap.Start, mic.tap())
        assertFalse(mic.listening)
        assertTrue(mic.onMic(listening = true, reason = null))
        return mic
    }
}
