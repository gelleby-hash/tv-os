package com.tvos.remote

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import okhttp3.Protocol as HttpProtocol
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class HoldRepeaterTest {
    @Test
    fun DelaysAreFirstThenSteady() {
        val repeater = HoldRepeater()
        assertEquals(0L, repeater.delayBefore(0))
        assertEquals(400L, repeater.delayBefore(1))
        assertEquals(120L, repeater.delayBefore(2))
        assertEquals(120L, repeater.delayBefore(10))
    }

    @Test
    fun HoldRepeatsUntilCancelled() = runTest {
        var presses = 0
        val hold = launch { HoldRepeater().hold { presses++; true } }
        runCurrent()
        assertEquals(1, presses) // the first press goes at once
        advanceTimeBy(400 + 120 * 3 + 1)
        assertEquals(5, presses)
        hold.cancel() // the finger lifted
        advanceTimeBy(10_000)
        assertEquals(5, presses)
    }

    // Review Focus 1: nothing keeps pressing after the connection drops. The fake socket would still take
    // presses after its close (as OkHttp's does until the close is answered), so only the client can stop them.
    @Test
    fun HoldStopsWhenDisconnected() = runTest {
        val socket = FakeSocket(closeAfter = 2)
        val client = RemoteClient("127.0.0.1:47800", {}, backgroundScope, socket)
        client.start()
        socket.open()
        assertTrue(client.connected.value)

        val hold = launch { HoldRepeater().hold { client.send(Protocol.press(RemoteKey.Up)) } }
        advanceTimeBy(10_000)

        assertEquals(2, socket.presses)
        assertTrue(hold.isCompleted)
        assertEquals(false, client.connected.value)
        client.stop()
    }

    /** A WebSocket factory with one socket that the launcher closes after [closeAfter] presses. */
    private class FakeSocket(private val closeAfter: Int) : WebSocket, WebSocket.Factory {
        private lateinit var request: Request
        private lateinit var listener: WebSocketListener
        var presses = 0

        override fun newWebSocket(request: Request, listener: WebSocketListener): WebSocket {
            this.request = request
            this.listener = listener
            return this
        }

        fun open() {
            val response = Response.Builder().request(request).protocol(HttpProtocol.HTTP_1_1)
                .code(101).message("Switching Protocols").build()
            listener.onOpen(this, response)
        }

        override fun send(text: String): Boolean {
            presses++
            if (presses == closeAfter)
                listener.onClosing(this, 1001, "launcher stopping")
            return true
        }

        override fun send(bytes: ByteString) = true
        override fun close(code: Int, reason: String?) = true
        override fun cancel() {}
        override fun queueSize() = 0L
        override fun request() = request
    }
}
