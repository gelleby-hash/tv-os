package com.tvos.remote

import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString.Companion.toByteString

/** The wait before each reconnect: 1, 2, 4 s, then every 5 s, so a launcher that comes up is found within ~5 s. */
class Backoff {
    private var attempt = 0

    fun next(): Long = minOf(1000L shl minOf(attempt++, 3), 5000L)

    fun reset() {
        attempt = 0
    }
}

/**
 * The connection to the launcher at `ws://<address>/remote/v1`. Between [start] and [stop] it keeps one
 * WebSocket open, reconnecting after a drop with [Backoff]; after [stop] it holds nothing. [send] goes only
 * over an open connection and says false otherwise, so a held key stops the moment the connection drops.
 * OkHttp sends no Origin header, which the launcher requires (it refuses any handshake that has one).
 */
class RemoteClient(
    private val address: String,
    private val listener: Listener,
    private val scope: CoroutineScope = MainScope(),
    private val sockets: WebSocket.Factory = http,
) {
    fun interface Listener {
        /** A message from the launcher, on OkHttp's thread. */
        fun onMessage(message: LauncherMessage)
    }

    private val lock = Any()
    private val backoff = Backoff()
    private var started = false
    private var socket: WebSocket? = null // the one being opened or open; callbacks from any other are stale
    private var retry: Job? = null
    private val _connected = MutableStateFlow(false)

    /** True while the WebSocket is open. */
    val connected: StateFlow<Boolean> = _connected.asStateFlow()

    fun start() {
        synchronized(lock) {
            if (started)
                return
            started = true
            backoff.reset()
            connect()
        }
    }

    fun stop() {
        synchronized(lock) {
            started = false
            retry?.cancel()
            retry = null
            // An open socket says goodbye; one still connecting is dropped, so nothing opens after the stop.
            socket?.let { if (_connected.value) it.close(1000, null) else it.cancel() }
            socket = null
            _connected.value = false
        }
    }

    fun send(text: String): Boolean = openSocket()?.send(text) ?: false

    fun send(bytes: ByteArray): Boolean = openSocket()?.send(bytes.toByteString()) ?: false

    private fun openSocket(): WebSocket? = synchronized(lock) { if (_connected.value) socket else null }

    // Called holding the lock. OkHttp connects in the background, so this returns at once.
    private fun connect() {
        // OkHttp's URL type spells ws:// as http://. Settings only saves a valid address, so null is a safeguard.
        val url = "http://$address${Protocol.PATH}".toHttpUrlOrNull() ?: return
        socket = sockets.newWebSocket(Request.Builder().url(url).build(), Events())
    }

    // Called holding the lock, once per lost socket.
    private fun dropped() {
        socket = null
        _connected.value = false
        if (!started)
            return
        val wait = backoff.next()
        retry = scope.launch {
            delay(wait)
            synchronized(lock) {
                if (started && socket == null)
                    connect()
            }
        }
    }

    private inner class Events : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            synchronized(lock) {
                if (webSocket !== socket)
                    return
                backoff.reset()
                _connected.value = true
            }
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (synchronized(lock) { webSocket !== socket })
                return
            Protocol.parse(text)?.let(listener::onMessage)
        }

        // The launcher is closing (it quit, or refused a frame): stop sending now, not when the close completes.
        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            synchronized(lock) {
                webSocket.close(1000, null)
                if (webSocket === socket)
                    dropped()
            }
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            synchronized(lock) {
                if (webSocket === socket)
                    dropped()
            }
        }

        // Refused, unreachable, or no answer to a ping.
        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            synchronized(lock) {
                if (webSocket === socket)
                    dropped()
            }
        }
    }

    private companion object {
        // A ping every 5 s finds a mini PC that was switched off or a Wi-Fi that went within ~10 s. It runs only
        // while the app is on screen, so the launcher pays nothing while the phone is in a pocket.
        val http: OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(3, TimeUnit.SECONDS)
            .pingInterval(5, TimeUnit.SECONDS)
            .build()
    }
}
