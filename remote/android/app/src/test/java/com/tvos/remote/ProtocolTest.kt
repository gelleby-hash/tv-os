package com.tvos.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ProtocolTest {
    @Test
    fun PressIsOneJsonMessage() {
        assertEquals("""{"v":1,"type":"press","key":"up"}""", Protocol.press(RemoteKey.Up))
    }

    @Test
    fun WireNamesMatchTheLauncher() {
        // launcher/src/TvLauncher/Remote/RemoteProtocol.cs, in its order: exact and case-sensitive.
        assertEquals(
            listOf("up", "down", "left", "right", "ok", "back", "home", "playPause", "volumeUp", "volumeDown"),
            RemoteKey.entries.map { it.wire },
        )
    }

    @Test
    fun MicMessages() {
        assertEquals("""{"v":1,"type":"micStart"}""", Protocol.micStart())
        assertEquals("""{"v":1,"type":"micStop"}""", Protocol.micStop())
    }

    @Test
    fun ParsesTheLauncherMessages() {
        // Exactly what RemoteProtocol.State and RemoteProtocol.Mic write.
        assertEquals(
            LauncherMessage.State("youtube", true),
            Protocol.parse("""{"v":1,"type":"state","app":"youtube","micAvailable":true}"""),
        )
        assertEquals(
            LauncherMessage.Mic(false, "timeout"),
            Protocol.parse("""{"v":1,"type":"mic","listening":false,"reason":"timeout"}"""),
        )
    }

    @Test
    fun ParsesNullsAsNull() {
        assertEquals(
            LauncherMessage.State(null, false),
            Protocol.parse("""{"v":1,"type":"state","app":null,"micAvailable":false}"""),
        )
        assertEquals(
            LauncherMessage.Mic(true, null),
            Protocol.parse("""{"v":1,"type":"mic","listening":true,"reason":null}"""),
        )
    }

    @Test
    fun RefusesJunkAndOtherVersions() {
        assertNull(Protocol.parse("junk"))
        assertNull(Protocol.parse(""))
        assertNull(Protocol.parse("[1]"))
        assertNull(Protocol.parse("""{"v":2,"type":"state","app":"youtube","micAvailable":true}"""))
        assertNull(Protocol.parse("""{"v":"1","type":"state","app":"youtube","micAvailable":true}"""))
        assertNull(Protocol.parse("""{"type":"state","app":"youtube","micAvailable":true}"""))
        assertNull(Protocol.parse("""{"v":1,"type":"power"}"""))
        assertNull(Protocol.parse("""{"v":1,"type":"state","app":"youtube","micAvailable":"yes"}"""))
        assertNull(Protocol.parse("""{"v":1,"type":"mic","reason":"timeout"}"""))
    }
}
