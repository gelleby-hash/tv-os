package com.tvos.remote

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Job

// The colours of the fourth design sketch (2026-10-31).
private val Screen = Color(0xFF141414)
private val Button = Color(0xFF2B2B2B)
private val ButtonDown = Color(0xFF3A3A3A)
private val Ink = Color(0xFFEEEEEE)
private val Muted = Color(0xFF9A9A9A)
private val PadLight = Color(0xFF303030)
private val PadDark = Color(0xFF1F1F1F)
private val Arrow = Color(0xFFBBBBBB)
private val OkFace = Color(0xFF232323)
private val OkText = Color(0xFF888888)
private val HomeBlue = Color(0xFF4AA8FF)
private val Online = Color(0xFF3ECF6A)
private val Offline = Color(0xFFFF6680)
private val Live = Color(0xFFFF0033)
private val HaloNear = Color(0x33FF0033) // the mockup's rgba(255,0,51,.2), 10 px out
private val HaloFar = Color(0x14FF0033) // rgba(255,0,51,.08), 22 px out
private val Notice = Color(0xFFFF6680) // the mockup's .pr-status line

/** Starts a press of a key; for a key that repeats, the job to cancel when the finger lifts. */
typealias Press = (key: RemoteKey, repeats: Boolean) -> Job?

/**
 * The one screen: the status line, the microphone with its line under it, the round D-pad with OK, then Back,
 * Home and play/pause, and the volume rocker. A press goes on touch down, like a remote's button.
 */
@Composable
fun RemoteScreen(
    connected: Boolean,
    address: String,
    press: Press,
    changeAddress: (String) -> Unit,
    mic: MicLook,
    tapMic: () -> Unit,
) {
    var editing by remember { mutableStateOf(false) }
    // The dark background runs under the system bars; the controls keep clear of them.
    Column(Modifier.fillMaxSize().background(Screen).safeDrawingPadding(), horizontalAlignment = Alignment.CenterHorizontally) {
        StatusLine(connected, Settings.nameOf(address) ?: address, onLongPress = { editing = true })
        // The mockup's spacing, with the controls in the middle of a taller screen where the thumb reaches.
        Column(
            Modifier.fillMaxWidth().weight(1f),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            Microphone(mic, tapMic)
            MicLine(mic.line)
            DPad(press)
            Row(Modifier.width(300.dp).padding(start = 30.dp, top = 22.dp, end = 30.dp), Arrangement.SpaceBetween) {
                RoundKey(RemoteKey.Back, "Back", press) { Glyph(BackIcon, Ink) }
                RoundKey(RemoteKey.Home, "Home", press) { Glyph(HomeIcon, HomeBlue) }
                RoundKey(RemoteKey.PlayPause, "Play or pause", press) { Glyph(PlayPauseIcon, Ink) }
            }
            VolumeRocker(press)
            Spacer(Modifier.height(40.dp))
        }
    }
    if (editing)
        AddressDialog(address, onDone = { editing = false }, changeAddress)
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun StatusLine(connected: Boolean, place: String, onLongPress: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .combinedClickable(onClick = {}, onLongClickLabel = "Choose what to connect to", onLongClick = onLongPress)
            .padding(horizontal = 22.dp, vertical = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(8.dp).background(if (connected) Online else Offline, CircleShape))
        Spacer(Modifier.width(6.dp))
        Text(if (connected) "$place connected" else "$place not connected", color = Muted, fontSize = 12.sp)
        Spacer(Modifier.weight(1f))
        Text("TV remote", color = Muted, fontSize = 12.sp)
    }
}

/** The mockup's microphone: grey at rest, greyed when it would do nothing, red with a soft halo while listening. */
@Composable
private fun Microphone(look: MicLook, tap: () -> Unit) {
    val lit = if (look.listening) {
        Modifier.drawBehind {
            val radius = size.minDimension / 2
            drawCircle(HaloFar, radius + 22.dp.toPx())
            drawCircle(HaloNear, radius + 10.dp.toPx())
        }
    } else {
        Modifier.shadow(6.dp, CircleShape)
    }
    Box(
        Modifier
            .size(64.dp)
            .then(lit)
            .clip(CircleShape)
            .background(if (look.listening) Live else Button)
            .clickable(enabled = look.enabled, role = Role.Button, onClick = tap)
            .semantics {
                contentDescription = "Microphone"
                if (look.listening)
                    stateDescription = "Listening"
            },
        contentAlignment = Alignment.Center,
    ) {
        val tint = when {
            look.listening -> Color.White
            look.enabled -> Ink
            else -> Ink.copy(alpha = 0.3f)
        }
        Icon(MicIcon, contentDescription = null, Modifier.size(40.dp), tint = tint)
    }
}

/** The mockup's line under the microphone (10 px down, 14 px high): "Listening... speak now", or why it did not. */
@Composable
private fun MicLine(line: String?) {
    Box(Modifier.fillMaxWidth().height(24.dp).padding(top = 10.dp), contentAlignment = Alignment.TopCenter) {
        if (line != null)
            Text(line, color = Notice, fontSize = 12.sp, lineHeight = 14.sp, maxLines = 1)
    }
}

@Composable
private fun DPad(press: Press) {
    Box(
        Modifier
            .size(230.dp)
            .shadow(12.dp, CircleShape)
            .drawBehind {
                // CSS radial-gradient(circle at 50% 40%, ...): the light spot a little above the middle.
                val centre = Offset(size.width / 2, size.height * 0.4f)
                drawCircle(Brush.radialGradient(listOf(PadLight, PadDark), centre, radius = size.width * 0.78f))
            },
    ) {
        PadKey(RemoteKey.Up, "Up", 0f, Modifier.align(Alignment.TopCenter).size(110.dp, 67.dp), press)
        PadKey(RemoteKey.Down, "Down", 180f, Modifier.align(Alignment.BottomCenter).size(110.dp, 67.dp), press)
        PadKey(RemoteKey.Left, "Left", 270f, Modifier.align(Alignment.CenterStart).size(67.dp, 110.dp), press)
        PadKey(RemoteKey.Right, "Right", 90f, Modifier.align(Alignment.CenterEnd).size(67.dp, 110.dp), press)
        Key(RemoteKey.Ok, "OK", repeats = false, press, Modifier.align(Alignment.Center).size(96.dp), CircleShape, OkFace) {
            Text("OK", color = OkText, fontSize = 14.sp)
        }
    }
}

@Composable
private fun PadKey(key: RemoteKey, label: String, turn: Float, modifier: Modifier, press: Press) {
    Key(key, label, repeats = true, press, modifier, shape = null, face = Color.Transparent) { down ->
        Glyph(TriangleIcon, if (down) Ink else Arrow, Modifier.size(18.dp).rotate(turn))
    }
}

@Composable
private fun RoundKey(key: RemoteKey, label: String, press: Press, content: @Composable () -> Unit) {
    Key(key, label, repeats = false, press, Modifier.size(60.dp).shadow(6.dp, CircleShape), CircleShape, Button) {
        content()
    }
}

@Composable
private fun VolumeRocker(press: Press) {
    Row(
        Modifier
            .padding(top = 22.dp)
            .size(170.dp, 56.dp)
            .shadow(6.dp, RoundedCornerShape(28.dp))
            .background(Button, RoundedCornerShape(28.dp)),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        val half = Modifier.weight(1f).fillMaxHeight()
        Key(RemoteKey.VolumeDown, "Volume down", repeats = true, press, half, RoundedCornerShape(28.dp, 0.dp, 0.dp, 28.dp)) {
            Text("−", color = Ink, fontSize = 24.sp)
        }
        Text("VOL", color = Muted, fontSize = 13.sp, modifier = Modifier.clearAndSetSemantics {})
        Key(RemoteKey.VolumeUp, "Volume up", repeats = true, press, half, RoundedCornerShape(0.dp, 28.dp, 28.dp, 0.dp)) {
            Text("+", color = Ink, fontSize = 24.sp)
        }
    }
}

/**
 * One key. It presses on touch down and, if it [repeats], keeps pressing until the finger lifts or the touch is
 * cancelled. TalkBack's double tap (the semantics click) gives one press. While down, the face lightens.
 */
@Composable
private fun Key(
    key: RemoteKey,
    label: String,
    repeats: Boolean,
    press: Press,
    modifier: Modifier,
    shape: Shape?,
    face: Color = Color.Transparent,
    content: @Composable (down: Boolean) -> Unit,
) {
    var down by remember { mutableStateOf(false) }
    val background = when {
        shape == null -> Modifier
        down -> Modifier.background(ButtonDown, shape)
        else -> Modifier.background(face, shape)
    }
    Box(
        modifier
            .then(background)
            .clearAndSetSemantics {
                contentDescription = label
                role = Role.Button
                onClick { press(key, false); true }
            }
            .pointerInput(key, repeats) {
                detectTapGestures(onPress = {
                    down = true
                    val hold = press(key, repeats)
                    try {
                        tryAwaitRelease()
                    } finally {
                        hold?.cancel()
                        down = false
                    }
                })
            },
        contentAlignment = Alignment.Center,
    ) {
        content(down)
    }
}

@Composable
private fun Glyph(icon: ImageVector, tint: Color, modifier: Modifier = Modifier.size(26.dp)) {
    Icon(icon, contentDescription = null, modifier, tint = tint)
}

@Composable
private fun AddressDialog(address: String, onDone: () -> Unit, changeAddress: (String) -> Unit) {
    var text by remember { mutableStateOf(address) }
    val typed = text.trim()
    val valid = Settings.isValidAddress(typed)
    val reachable = valid && Settings.isReachable(typed)
    AlertDialog(
        onDismissRequest = onDone,
        title = { Text("Connect to") },
        text = {
            Column {
                // Pick a known launcher by name, or type an address below; picking one fills the box.
                for (place in Settings.PLACES) {
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .selectable(selected = typed == place.address, onClick = { text = place.address }, role = Role.RadioButton)
                            .padding(vertical = 6.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        RadioButton(selected = typed == place.address, onClick = null)
                        Spacer(Modifier.width(8.dp))
                        Column {
                            Text(place.name)
                            Text(place.address, fontSize = 12.sp)
                        }
                    }
                }
                Spacer(Modifier.height(8.dp))
                OutlinedTextField(
                    value = text,
                    onValueChange = { text = it },
                    label = { Text("Address and port") },
                    supportingText = {
                        Text(
                            when {
                                !valid -> "For example ${Settings.DEFAULT_ADDRESS}"
                                !reachable -> "This app may only reach the addresses above"
                                else -> Settings.nameOf(typed) ?: "Another port on a known address"
                            },
                        )
                    },
                    isError = !reachable,
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                )
            }
        },
        confirmButton = {
            TextButton(enabled = reachable, onClick = { changeAddress(typed); onDone() }) { Text("Save") }
        },
        dismissButton = { TextButton(onClick = onDone) { Text("Cancel") } },
    )
}

// The mockup's text glyphs (an arrow, a house, play/pause) are drawn here, so no phone font can show them as
// emoji or as empty boxes. 24 by 24, black; an Icon tints them.
private fun icon(name: String, build: ImageVector.Builder.() -> Unit): ImageVector =
    ImageVector.Builder(name, 24.dp, 24.dp, 24f, 24f).apply(build).build()

private val Black = SolidColor(Color.Black)

private val TriangleIcon = icon("Triangle") {
    addPath(addPathNodes("M12 6 L19 17 L5 17 Z"), fill = Black)
}

private val BackIcon = icon("Back") {
    addPath(
        addPathNodes("M19 17 A7.5 7.5 0 1 0 5.5 11.5 M2.5 8.5 L5.5 12.5 L9 9"),
        stroke = Black,
        strokeLineWidth = 2.2f,
        strokeLineCap = StrokeCap.Round,
        strokeLineJoin = StrokeJoin.Round,
    )
}

private val HomeIcon = icon("Home") {
    addPath(
        addPathNodes("M4 11 L12 4 L20 11 L20 20 L4 20 Z"),
        stroke = Black,
        strokeLineWidth = 2.2f,
        strokeLineJoin = StrokeJoin.Round,
    )
}

private val PlayPauseIcon = icon("PlayPause") {
    addPath(addPathNodes("M2 6 L2 18 L11 12 Z M14 6 L17 6 L17 18 L14 18 Z M19 6 L22 6 L22 18 L19 18 Z"), fill = Black)
}
