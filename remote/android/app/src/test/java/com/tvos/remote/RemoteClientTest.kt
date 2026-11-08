package com.tvos.remote

import java.net.InetAddress
import java.net.ServerSocket
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class RemoteClientTest {
    // Review Focus 2.
    @Test
    fun BackoffIsCappedAndResets() {
        val backoff = Backoff()
        assertEquals(listOf(1000L, 2000L, 4000L, 5000L, 5000L), List(5) { backoff.next() })
        backoff.reset()
        assertEquals(1000L, backoff.next())
    }

    // The launcher refuses an upgrade with an Origin header (RemoteServer.Check); OkHttp sends none by default.
    // This reads OkHttp's real request on a loopback socket, so a change that adds one fails here, not at the TV.
    @Test
    fun HandshakeIsWhatTheLauncherAccepts() {
        val scope = CoroutineScope(SupervisorJob())
        ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { server ->
            server.soTimeout = 5000
            val client = RemoteClient("127.0.0.1:${server.localPort}", {}, scope)
            client.start()
            try {
                server.accept().use { connection ->
                    connection.soTimeout = 5000
                    val reader = connection.getInputStream().bufferedReader(Charsets.ISO_8859_1)
                    val lines = generateSequence { reader.readLine() }.takeWhile { it.isNotEmpty() }.toList()
                    assertEquals("GET /remote/v1 HTTP/1.1", lines.first())
                    assertTrue(lines.none { it.startsWith("Origin:", ignoreCase = true) })
                    assertTrue(lines.any { it.equals("Sec-WebSocket-Version: 13", ignoreCase = true) })
                }
            } finally {
                client.stop()
                scope.cancel()
            }
        }
    }
}
