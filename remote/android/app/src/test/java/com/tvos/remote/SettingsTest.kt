package com.tvos.remote

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SettingsTest {
    // A list as the build writes it from launchers.properties.
    private val places = Settings.parsePlaces("TV|192.168.0.10:47800;Dev PC|192.168.0.20:47800") + Settings.USB

    @Test
    fun AddressIsHostAndPort() {
        assertTrue(Settings.isValidAddress(Settings.DEFAULT_ADDRESS))
        assertTrue(Settings.isValidAddress("127.0.0.1:47800"))
        assertFalse(Settings.isValidAddress("192.168.0.10"))
        assertFalse(Settings.isValidAddress("192.168.0.10:"))
        assertFalse(Settings.isValidAddress("192.168.0.10:0"))
        assertFalse(Settings.isValidAddress("192.168.0.10:65536"))
        assertFalse(Settings.isValidAddress("ws://192.168.0.10:47800"))
        assertFalse(Settings.isValidAddress("192.168.0.10:47800/remote/v1"))
        assertFalse(Settings.isValidAddress(" 192.168.0.10:47800"))
        assertFalse(Settings.isValidAddress(""))
        // Nothing but host and port: a fragment, query or user part would make OkHttp connect elsewhere.
        assertFalse(Settings.isValidAddress("192.168.0.10#:47800"))
        assertFalse(Settings.isValidAddress("192.168.0.10?:47800"))
        assertFalse(Settings.isValidAddress("me@192.168.0.10:47800"))
        assertTrue(Settings.isValidAddress("tv-box:47800"))
    }

    @Test
    fun PlacesAreTheBuildsListThenUsb() {
        assertEquals(
            listOf(
                Settings.Place("TV", "192.168.0.10:47800"),
                Settings.Place("Dev PC", "192.168.0.20:47800"),
                Settings.Place("Dev PC over USB", "127.0.0.1:47800"),
            ),
            places,
        )
    }

    @Test
    fun BuiltPlacesAreValidEndWithUsbAndStartWithTheDefault() {
        assertTrue(Settings.PLACES.all { Settings.isValidAddress(it.address) })
        assertTrue(Settings.PLACES.size >= 2)
        assertEquals(Settings.USB, Settings.PLACES.last())
        assertEquals(Settings.DEFAULT_ADDRESS, Settings.PLACES.first().address)
    }

    @Test
    fun ParsePlacesSkipsEmptyParts() {
        assertEquals(listOf(Settings.Place("TV", "tv-box:47800")), Settings.parsePlaces(";TV|tv-box:47800;"))
        assertEquals(emptyList<Settings.Place>(), Settings.parsePlaces(""))
    }

    @Test
    fun NameOfKnownAddressesOnly() {
        assertEquals("TV", Settings.nameOf("192.168.0.10:47800", places))
        assertEquals("Dev PC", Settings.nameOf("192.168.0.20:47800", places))
        assertEquals("Dev PC over USB", Settings.nameOf("127.0.0.1:47800", places))
        assertNull(Settings.nameOf("192.168.0.10:47801", places))
        assertNull(Settings.nameOf("192.168.0.50:47800", places))
    }

    @Test
    fun ReachableHostsAreThoseOfThePlaces() {
        assertTrue(Settings.isReachable("192.168.0.10:47801", places))
        assertTrue(Settings.isReachable("127.0.0.1:9000", places))
        assertFalse(Settings.isReachable("192.168.0.50:47800", places))
        assertFalse(Settings.isReachable("tv-box:47800", places))
        assertFalse(Settings.isReachable("not an address", places))
    }
}
