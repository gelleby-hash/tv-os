package com.tvos.remote

import org.json.JSONException
import org.json.JSONObject

/** The keys the phone can press: the Rii i25's own, nothing more (spec goal 2). [wire] is the launcher's name. */
enum class RemoteKey(val wire: String) {
    Up("up"),
    Down("down"),
    Left("left"),
    Right("right"),
    Ok("ok"),
    Back("back"),
    Home("home"),
    PlayPause("playPause"),
    VolumeUp("volumeUp"),
    VolumeDown("volumeDown"),
}

/** A message from the launcher. */
sealed interface LauncherMessage {
    /** What is in front ([app], null on the home screen) and whether its mic button works. */
    data class State(val app: String?, val micAvailable: Boolean) : LauncherMessage

    /** Whether a listening session runs, and why it ended (for example "timeout"), or null. */
    data class Mic(val listening: Boolean, val reason: String?) : LauncherMessage
}

/**
 * The phone remote's wire format, version 1, as the launcher defines it in
 * `launcher/src/TvLauncher/Remote/RemoteProtocol.cs`. Text frames are one JSON object each; binary frames
 * (later) carry audio. Anything from the launcher that is not a known message of this version parses to null.
 */
object Protocol {
    const val VERSION = 1
    const val PATH = "/remote/v1"

    // The wire names are fixed ASCII, so the messages are written as text with nothing to escape.
    fun press(key: RemoteKey): String = """{"v":$VERSION,"type":"press","key":"${key.wire}"}"""

    fun micStart(): String = """{"v":$VERSION,"type":"micStart"}"""

    fun micStop(): String = """{"v":$VERSION,"type":"micStop"}"""

    fun parse(json: String): LauncherMessage? {
        val root = try {
            JSONObject(json)
        } catch (e: JSONException) {
            return null
        }
        // An Int, as the launcher's TryGetInt32 wants: "1" or 1.5 are another message, not version 1.
        if (root.opt("v") != VERSION)
            return null
        return when (root.opt("type") as? String) {
            "state" -> {
                val app = textOrNull(root, "app") ?: return null
                val micAvailable = root.opt("micAvailable") as? Boolean ?: return null
                LauncherMessage.State(app.value, micAvailable)
            }
            "mic" -> {
                val listening = root.opt("listening") as? Boolean ?: return null
                val reason = textOrNull(root, "reason") ?: return null
                LauncherMessage.Mic(listening, reason.value)
            }
            else -> null
        }
    }

    private class Text(val value: String?)

    /** A string or JSON null, boxed so that a missing field or another type (null here) can be told from it. */
    private fun textOrNull(root: JSONObject, name: String): Text? {
        val value = root.opt(name)
        return if (value == JSONObject.NULL) Text(null) else (value as? String)?.let(::Text)
    }
}
