package com.tvos.remote

/** What the microphone button shows: whether a tap does anything, red while listening, and the line under it. */
data class MicLook(val enabled: Boolean, val listening: Boolean, val line: String?)

/**
 * The microphone button's state, from the taps and the launcher's messages; the view model does the sending
 * and the recording. A tap starts a session only when the last `state` said `micAvailable`. The phone records
 * only after the launcher's `mic` with `listening: true`, and only if that answers this phone's own start.
 * After a stop it waits for the launcher's `mic` with `listening: false`, which every end of a session sends,
 * so a start that crossed the stop cannot turn the microphone on. While this phone listens the launcher says
 * `micAvailable: false` to every phone, this one included, so the stop does not depend on it (ruling R13).
 */
class MicButton {
    enum class Tap { Start, Stop, Nothing }

    private enum class Phase { Idle, Starting, Listening, Stopping }

    private var phase = Phase.Idle
    private var available = false
    private var note: String? = null

    /** True while the phone should record. */
    val listening: Boolean get() = phase == Phase.Listening

    fun look(connected: Boolean): MicLook {
        val enabled = connected && when (phase) {
            Phase.Idle -> available
            Phase.Starting, Phase.Listening -> true
            Phase.Stopping -> false
        }
        return MicLook(enabled, listening, if (listening) LISTENING else note)
    }

    fun onState(micAvailable: Boolean) {
        available = micAvailable
    }

    /** A tap on the button; the caller sends `micStart` or `micStop` as it says. */
    fun tap(): Tap = when (phase) {
        Phase.Idle -> if (available) {
            note = null
            phase = Phase.Starting
            Tap.Start
        } else {
            Tap.Nothing
        }
        Phase.Starting, Phase.Listening -> {
            phase = Phase.Stopping
            Tap.Stop
        }
        Phase.Stopping -> Tap.Nothing
    }

    /** The launcher's `mic` message. True when the phone should start recording now. */
    fun onMic(listening: Boolean, reason: String?): Boolean {
        if (listening) {
            if (phase != Phase.Starting)
                return false
            phase = Phase.Listening
            return true
        }
        if (phase == Phase.Idle)
            return false
        phase = Phase.Idle
        REFUSALS[reason]?.let { note = it }
        return false
    }

    /**
     * The phone's microphone would not open, or stopped giving sound. While this phone listens it stops, with a
     * line saying so, and the caller sends `micStop`. Otherwise the launcher has already ended the session and
     * nothing more is said.
     */
    fun captureFailed(): Tap {
        if (phase != Phase.Listening)
            return Tap.Nothing
        phase = Phase.Stopping
        note = DID_NOT_START
        return Tap.Stop
    }

    /** A line of the phone's own: the microphone not allowed. The next tap clears it. */
    fun refused(line: String) {
        note = line
    }

    /** The connection went (or the app left the screen): the launcher has ended any session by itself. */
    fun reset() {
        phase = Phase.Idle
        available = false
        note = null
    }

    companion object {
        const val LISTENING = "Listening... speak now"
        const val DID_NOT_START = "Voice search didn't start"
        const val NOT_ALLOWED = "Microphone not allowed"

        // The launcher's reasons for refusing a start (PhoneMicSession.StartAsync); its other reasons end a
        // session that ran, and the button just goes back.
        private val REFUSALS = mapOf(
            // Said plainly: the owner's own phone can be the busy one, after a Wi-Fi blip left its old session open.
            "busy" to "Voice search busy, try again",
            "not-youtube" to "Open YouTube first",
            "failed" to DID_NOT_START,
        )
    }
}
