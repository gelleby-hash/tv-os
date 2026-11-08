package com.tvos.remote

import kotlinx.coroutines.delay

/**
 * Holding a button presses it again and again, like holding a remote's button: once at once, again after
 * [firstDelayMs], then every [everyMs]. Every repeat is a whole press; the launcher never sees a key held down.
 */
class HoldRepeater(private val firstDelayMs: Long = 400, private val everyMs: Long = 120) {
    /** How long to wait before press [n], counting from 0. */
    fun delayBefore(n: Int): Long = when (n) {
        0 -> 0
        1 -> firstDelayMs
        else -> everyMs
    }

    /**
     * Presses until [press] says it could not send (the connection dropped) or the caller cancels (the finger
     * lifted). Stopping on a failed send means a reconnect never picks up a press that was left running.
     */
    suspend fun hold(press: () -> Boolean) {
        var n = 0
        while (true) {
            delay(delayBefore(n++))
            if (!press())
                return
        }
    }
}
