package com.tvos.remote

import android.app.Application
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * The remote's state, its presses and its microphone. It observes the activity's lifecycle: connected while the
 * app is on screen (onStart), nothing held while it is not (onStop), so neither the phone nor the launcher keeps
 * an idle connection (spec, "The app connects only while it is on screen"). The microphone never records in
 * the background: it stops on the launcher's end of a session, a second tap, a lost connection or onStop,
 * whichever comes first. Everything here runs on the main thread; the client's messages are posted to it.
 */
class RemoteViewModel(application: Application) : AndroidViewModel(application), DefaultLifecycleObserver {
    private val settings = Settings(application)
    private val repeater = HoldRepeater()
    private val vibrator: Vibrator? = application.getSystemService(Vibrator::class.java)
    private val mic = MicButton()
    private val capture = MicCapture()
    private var started = false
    private var client = newClient(settings.address)
    private var following: Job? = null
    private var recordings = 0

    private val _connected = MutableStateFlow(false)
    val connected: StateFlow<Boolean> = _connected.asStateFlow()

    private val _address = MutableStateFlow(settings.address)
    val address: StateFlow<String> = _address.asStateFlow()

    private val _micLook = MutableStateFlow(mic.look(connected = false))
    val micLook: StateFlow<MicLook> = _micLook.asStateFlow()

    init {
        follow()
    }

    override fun onStart(owner: LifecycleOwner) {
        started = true
        client.start()
    }

    override fun onStop(owner: LifecycleOwner) {
        started = false
        client.stop()
        mic.reset()
        update()
    }

    override fun onCleared() {
        client.stop()
        capture.stop()
    }

    /**
     * Presses [key] once now. For a key that [repeats] (arrows, volume) it goes on pressing until the returned
     * job is cancelled (the finger lifted) or a press cannot be sent (the connection dropped).
     */
    fun press(key: RemoteKey, repeats: Boolean): Job? {
        if (!repeats) {
            send(key)
            return null
        }
        return viewModelScope.launch { repeater.hold { send(key) } }
    }

    /** Points the app at another launcher: the mini PC, or the dev PC or `127.0.0.1` for testing. */
    fun changeAddress(value: String) {
        if (value == settings.address)
            return
        settings.address = value
        _address.value = value
        client.stop()
        mic.reset()
        update()
        client = newClient(value)
        follow()
        if (started)
            client.start()
    }

    /**
     * A tap on the microphone, once RECORD_AUDIO is granted. It starts or stops a voice search; recording waits
     * for the launcher's go-ahead.
     */
    fun micTap() {
        when (mic.tap()) {
            MicButton.Tap.Start -> if (!client.send(Protocol.micStart())) mic.reset()
            MicButton.Tap.Stop -> client.send(Protocol.micStop())
            MicButton.Tap.Nothing -> {}
        }
        update()
    }

    /** The owner refused the microphone: say so, and do nothing else. */
    fun micNotAllowed() {
        mic.refused(MicButton.NOT_ALLOWED)
        update()
    }

    // The client calls back on OkHttp's thread; the message is handled on the main thread, and only if it comes
    // from the client in use (not one replaced by changeAddress).
    private fun newClient(address: String): RemoteClient {
        lateinit var made: RemoteClient
        made = RemoteClient(address, { message ->
            viewModelScope.launch { if (made === client) onMessage(message) }
        }, viewModelScope)
        return made
    }

    private fun onMessage(message: LauncherMessage) {
        when (message) {
            is LauncherMessage.State -> mic.onState(message.micAvailable)
            is LauncherMessage.Mic -> if (mic.onMic(message.listening, message.reason)) record()
        }
        update()
    }

    // The launcher is listening for this phone: the sound goes as binary frames, straight from the recording
    // thread. A microphone that will not open, or stops giving sound, ends the session at once and says so. A
    // failure reported by an earlier recording is ignored.
    private fun record() {
        val current = client
        val recording = ++recordings
        val started = capture.start(
            onChunk = { current.send(it) },
            onFailed = { viewModelScope.launch { if (recording == recordings) micFailed() } },
        )
        if (!started)
            micFailed()
    }

    // Sends micStop only if this phone still listens; if the launcher already ended the session, it ends quietly.
    // Either way update() then stops the recording.
    private fun micFailed() {
        if (mic.captureFailed() == MicButton.Tap.Stop)
            client.send(Protocol.micStop())
        update()
    }

    // Called after every change: recording runs only while the button says listening, and the screen follows.
    private fun update() {
        if (!mic.listening)
            capture.stop()
        _micLook.value = mic.look(_connected.value)
    }

    private fun follow() {
        following?.cancel()
        val current = client
        following = viewModelScope.launch {
            current.connected.collect {
                _connected.value = it
                // A lost connection ends the launcher's session without a word, so the phone ends its own.
                if (!it)
                    mic.reset()
                update()
            }
        }
    }

    // Each press that went gives a short buzz (spec goal 4); one that could not be sent gives none.
    private fun send(key: RemoteKey): Boolean {
        val sent = client.send(Protocol.press(key))
        if (sent)
            buzz()
        return sent
    }

    private fun buzz() {
        val effect = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q)
            VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK)
        else
            VibrationEffect.createOneShot(10, VibrationEffect.DEFAULT_AMPLITUDE)
        vibrator?.vibrate(effect)
    }
}
