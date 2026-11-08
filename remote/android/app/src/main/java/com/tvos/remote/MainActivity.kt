package com.tvos.remote

import android.Manifest
import android.content.pm.PackageManager
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue

class MainActivity : ComponentActivity() {
    private val remote: RemoteViewModel by viewModels()

    // Asked on the first tap of the microphone, not at start, so the remote's buttons never wait on it.
    private val askForMic = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) remote.micTap() else remote.micNotAllowed()
    }

    // Android 17 (API 37) lets an app reach devices on the phone's own network only with this runtime permission;
    // without it the mini PC, on the phone's subnet, was unreachable while the dev PC, on another subnet, was not
    // (owner's S24 FE, 2026-10-31). The client keeps retrying, so a grant connects within its back-off.
    private val askForLocalNetwork = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= 37 &&
            checkSelfPermission(Manifest.permission.ACCESS_LOCAL_NETWORK) != PackageManager.PERMISSION_GRANTED)
            askForLocalNetwork.launch(Manifest.permission.ACCESS_LOCAL_NETWORK)
        // Light status and navigation bar icons on the dark screen.
        enableEdgeToEdge(SystemBarStyle.dark(Color.TRANSPARENT), SystemBarStyle.dark(Color.TRANSPARENT))
        lifecycle.addObserver(remote)
        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                val connected by remote.connected.collectAsState()
                val address by remote.address.collectAsState()
                val mic by remote.micLook.collectAsState()
                RemoteScreen(connected, address, remote::press, remote::changeAddress, mic, ::tapMic)
            }
        }
    }

    private fun tapMic() {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED)
            remote.micTap()
        else
            askForMic.launch(Manifest.permission.RECORD_AUDIO)
    }
}
