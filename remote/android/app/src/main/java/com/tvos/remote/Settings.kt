package com.tvos.remote

import android.content.Context

/** The app's one setting: where the launcher listens, as `host:port`. */
class Settings(context: Context) {
    private val prefs = context.getSharedPreferences("remote", Context.MODE_PRIVATE)

    var address: String
        get() = prefs.getString(KEY_ADDRESS, null)?.takeIf(::isValidAddress) ?: DEFAULT_ADDRESS
        set(value) {
            require(isValidAddress(value)) { "Not a host:port address: $value" }
            prefs.edit().putString(KEY_ADDRESS, value).apply()
        }

    /** A launcher the owner can pick by name instead of typing its address. */
    data class Place(val name: String, val address: String)

    companion object {
        /** Loopback, for testing over a USB cable with `adb reverse tcp:47800 tcp:47800`. */
        val USB = Place("Dev PC over USB", "127.0.0.1:47800")

        /**
         * The launchers this app knows, in the order the address dialog lists them: those in launchers.properties
         * (written into BuildConfig.LAUNCHERS by app/build.gradle.kts), then [USB]. The build also writes their hosts
         * into the network security config, the only hosts the app may reach in the clear.
         */
        val PLACES = parsePlaces(BuildConfig.LAUNCHERS) + USB

        /** The first launcher in launchers.properties, used until another is picked. */
        val DEFAULT_ADDRESS = PLACES.first().address

        /** Reads the build's `name|host:port;name|host:port` list; the build has already checked every entry. */
        fun parsePlaces(text: String): List<Place> =
            text.split(';').filter { it.isNotEmpty() }.map { Place(it.substringBefore('|'), it.substringAfter('|')) }

        private const val KEY_ADDRESS = "address"

        // An IPv4 address or a host name (letters, digits, dots and hyphens only, so no scheme, path, query,
        // fragment or user part can ride along) and a port from 1 to 65535.
        private val HostAndPort = Regex("""([A-Za-z0-9.-]+):(\d{1,5})""")

        fun isValidAddress(address: String): Boolean {
            val port = HostAndPort.matchEntire(address)?.groupValues?.get(2)?.toInt() ?: return false
            return port in 1..65535
        }

        /** The name of a known launcher at [address], or null for an address the owner typed. */
        fun nameOf(address: String, places: List<Place> = PLACES): String? = places.firstOrNull { it.address == address }?.name

        /** Whether the network security config lets the app reach [address]'s host: the hosts of [PLACES] only. */
        fun isReachable(address: String, places: List<Place> = PLACES): Boolean {
            val host = HostAndPort.matchEntire(address)?.groupValues?.get(1) ?: return false
            return places.any { it.address.substringBefore(':') == host }
        }
    }
}
