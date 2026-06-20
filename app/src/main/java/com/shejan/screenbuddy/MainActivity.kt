package com.shejan.screenbuddy

import android.os.Bundle
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import com.google.android.gms.ads.AdRequest
import com.google.android.gms.ads.AdSize
import com.google.android.gms.ads.AdView
import com.google.android.gms.ads.MobileAds
import com.shejan.screenbuddy.ui.theme.ScreenBuddyTheme

class MainActivity : ComponentActivity() {

    private var player: H264StreamPlayer? = null

    // UI state states that Compose will observe
    private var isConnected = mutableStateOf(false)
    private var isConnecting = mutableStateOf(false)
    private var connectionError = mutableStateOf<String?>(null)
    private var videoWidth = mutableStateOf(16)
    private var videoHeight = mutableStateOf(9)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        // Keep screen on during mirroring sessions
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        // 1. Initialize Google Mobile Ads SDK (Test ads)
        MobileAds.initialize(this) {}

        setContent {
            ScreenBuddyTheme {
                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.background
                ) {
                    ScreenBuddyApp()
                }
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        stopMirroring()
    }

    private fun startMirroring(host: String, port: Int, pin: String, surface: Surface) {
        player?.stop()
        videoWidth.value = 16
        videoHeight.value = 9
        player = H264StreamPlayer(host, port, pin, surface, object : H264StreamPlayer.Callback {
            override fun onConnected() {
                runOnUiThread {
                    isConnecting.value = false
                    isConnected.value = true
                    connectionError.value = null
                }
            }

            override fun onDisconnected() {
                runOnUiThread {
                    isConnecting.value = false
                    isConnected.value = false
                }
            }

            override fun onError(e: Exception) {
                runOnUiThread {
                    isConnecting.value = false
                    isConnected.value = false
                    connectionError.value = e.message ?: "Connection error"
                }
            }

            override fun onVideoSizeChanged(width: Int, height: Int) {
                runOnUiThread {
                    videoWidth.value = width
                    videoHeight.value = height
                }
            }
        })
        player?.start()
    }

    private fun stopMirroring() {
        player?.stop()
        player = null
        isConnecting.value = false
        isConnected.value = false
    }

    @Composable
    fun ScreenBuddyApp() {
        var showMirrorView by remember { mutableStateOf(false) }
        var pairingPin by remember { mutableStateOf("") }
        
        // Discovered IP and Port
        var serverIp by remember { mutableStateOf("") }
        var serverPort by remember { mutableStateOf(7890) }

        // Observe global activity states
        val connected by isConnected
        val connecting by isConnecting
        val errorMsg by connectionError

        if (showMirrorView) {
            MirrorView(
                host = serverIp,
                port = serverPort,
                pin = pairingPin,
                onExit = {
                    stopMirroring()
                    showMirrorView = false
                }
            )
        } else {
            SetupView(
                pairingPin = pairingPin,
                connecting = connecting,
                errorMsg = errorMsg,
                onPinChange = { 
                    if (it.length <= 6 && it.all { char -> char.isDigit() }) {
                        pairingPin = it 
                    }
                },
                onConnectClick = {
                    connectionError.value = null
                    isConnecting.value = true

                    // Trigger UDP Auto-Discovery using the entered pairing PIN
                    PCDiscovery.discoverAndConnect(pairingPin, object : PCDiscovery.Callback {
                        override fun onDiscovered(ip: String, port: Int) {
                            runOnUiThread {
                                serverIp = ip
                                serverPort = port
                                // Connection details received, proceed to render view
                                showMirrorView = true
                            }
                        }

                        override fun onDiscoveryFailed(e: Exception) {
                            runOnUiThread {
                                isConnecting.value = false
                                connectionError.value = "PC Companion not found on local network. Verify your PIN and network connection."
                            }
                        }
                    })
                }
            )
        }
    }

    @Composable
    fun SetupView(
        pairingPin: String,
        connecting: Boolean,
        errorMsg: String?,
        onPinChange: (String) -> Unit,
        onConnectClick: () -> Unit
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(
                    Brush.verticalGradient(
                        colors = listOf(
                            Color(0xFF0F2027),
                            Color(0xFF203A43),
                            Color(0xFF2C5364)
                        )
                    )
                )
        ) {
            // Main setup panel
            Column(
                horizontalAlignment = Alignment.CenterHorizontally,
                modifier = Modifier
                    .fillMaxSize()
                    .padding(24.dp)
                    .padding(bottom = 60.dp), // make space for bottom ad banner
                verticalArrangement = Arrangement.Center
            ) {
                // Header Title
                Text(
                    text = "ScreenBuddy",
                    fontSize = 38.sp,
                    fontWeight = FontWeight.Bold,
                    color = Color.White,
                    letterSpacing = 1.5.sp
                )
                Text(
                    text = "Secure Tablet Second Display",
                    fontSize = 14.sp,
                    color = Color.White.copy(alpha = 0.7f),
                    modifier = Modifier.padding(top = 4.dp, bottom = 32.dp)
                )

                // Input Card
                Column(
                    modifier = Modifier
                        .fillMaxWidth(0.85f)
                        .clip(RoundedCornerShape(24.dp))
                        .background(Color.White.copy(alpha = 0.08f))
                        .padding(24.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Text(
                        text = "Enter 6-digit PIN shown on Windows PC:",
                        fontSize = 13.sp,
                        color = Color.White.copy(alpha = 0.8f),
                        textAlign = TextAlign.Center,
                        modifier = Modifier.fillMaxWidth().padding(bottom = 16.dp)
                    )

                    // 6-digit numeric PIN field
                    OutlinedTextField(
                        value = pairingPin,
                        onValueChange = onPinChange,
                        label = { Text("Pairing PIN", color = Color.White.copy(alpha = 0.6f)) },
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedTextColor = Color.White,
                            unfocusedTextColor = Color.White,
                            focusedBorderColor = Color(0xFF00ADB5),
                            unfocusedBorderColor = Color.White.copy(alpha = 0.3f),
                            focusedLabelColor = Color(0xFF00ADB5)
                        ),
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        modifier = Modifier.fillMaxWidth()
                    )

                    Spacer(modifier = Modifier.height(24.dp))

                    // Connect button
                    Button(
                        onClick = onConnectClick,
                        enabled = !connecting && pairingPin.length == 6,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = Color(0xFF00ADB5),
                            contentColor = Color.White,
                            disabledContainerColor = Color(0xFF00ADB5).copy(alpha = 0.4f)
                        ),
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(50.dp)
                    ) {
                        if (connecting) {
                            CircularProgressIndicator(
                                color = Color.White,
                                modifier = Modifier.size(24.dp),
                                strokeWidth = 2.dp
                            )
                        } else {
                            Text("Connect Screen", fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                        }
                    }
                }

                // Error Message Section
                if (!errorMsg.isNullOrEmpty()) {
                    Spacer(modifier = Modifier.height(20.dp))
                    Text(
                        text = errorMsg,
                        color = Color(0xFFFF5252),
                        fontSize = 13.sp,
                        textAlign = TextAlign.Center,
                        modifier = Modifier.fillMaxWidth(0.85f)
                    )
                }

                Spacer(modifier = Modifier.height(40.dp))
                
                Text(
                    text = "Ensure both devices are on the same WiFi network.",
                    fontSize = 12.sp,
                    color = Color.White.copy(alpha = 0.5f),
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth(0.8f)
                )
            }

            // Google AdMob test banner ad - only rendered at the bottom of the setup screen
            AdMobBanner(
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .height(50.dp)
                    .background(Color.Black.copy(alpha = 0.5f))
            )
        }
    }

    @Composable
    fun AdMobBanner(modifier: Modifier = Modifier) {
        AndroidView(
            modifier = modifier,
            factory = { context ->
                AdView(context).apply {
                    setAdSize(AdSize.BANNER)
                    // Official Google AdMob test banner ID
                    adUnitId = "ca-app-pub-3940256099942544/6300978111"
                    loadAd(AdRequest.Builder().build())
                }
            }
        )
    }

    @Composable
    fun MirrorView(
        host: String,
        port: Int,
        pin: String,
        onExit: () -> Unit
    ) {
        var showOverlay by remember { mutableStateOf(true) }

        // Fullscreen surface container
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black)
        ) {
            // Android native SurfaceView embedded in Compose
            val w by videoWidth
            val h by videoHeight
            val ratio = w.toFloat() / h.toFloat()

            AndroidView(
                factory = { context ->
                    val gd = android.view.GestureDetector(context, object : android.view.GestureDetector.SimpleOnGestureListener() {
                        override fun onDoubleTap(e: android.view.MotionEvent): Boolean {
                            showOverlay = !showOverlay
                            return true
                        }
                    })

                    SurfaceView(context).apply {
                        setOnTouchListener { view, event ->
                            gd.onTouchEvent(event)
                            val activePlayer = player
                            if (activePlayer != null) {
                                val action = when (event.actionMasked) {
                                    android.view.MotionEvent.ACTION_DOWN -> 0
                                    android.view.MotionEvent.ACTION_MOVE -> 1
                                    android.view.MotionEvent.ACTION_UP -> 2
                                    else -> -1
                                }
                                if (action != -1) {
                                    val normX = event.x / view.width
                                    val normY = event.y / view.height
                                    activePlayer.sendInputEvent(action, normX, normY)
                                }
                            }
                            true
                        }

                        holder.addCallback(object : SurfaceHolder.Callback {
                            override fun surfaceCreated(holder: SurfaceHolder) {
                                startMirroring(host, port, pin, holder.surface)
                            }

                            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

                            override fun surfaceDestroyed(holder: SurfaceHolder) {
                                stopMirroring()
                            }
                        })
                    }
                },
                modifier = Modifier
                    .aspectRatio(ratio)
                    .align(Alignment.Center)
            )

            // Auto-hiding / double-tap-triggered control overlay
            AnimatedVisibility(
                visible = showOverlay,
                enter = fadeIn(),
                exit = fadeOut(),
                modifier = Modifier.align(Alignment.BottomCenter)
            ) {
                Card(
                    shape = RoundedCornerShape(topStart = 16.dp, topEnd = 16.dp),
                    colors = CardDefaults.cardColors(
                        containerColor = Color.Black.copy(alpha = 0.75f)
                    ),
                    modifier = Modifier
                        .fillMaxWidth()
                        .wrapContentHeight()
                ) {
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 24.dp, vertical = 16.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.SpaceBetween
                    ) {
                        Column {
                            Text(
                                text = "Mirroring: $host:$port",
                                color = Color.White,
                                fontSize = 14.sp,
                                fontWeight = FontWeight.Medium
                            )
                            Text(
                                text = "Double-tap screen to hide/show controls",
                                color = Color.White.copy(alpha = 0.5f),
                                fontSize = 11.sp
                            )
                        }

                        Button(
                            onClick = onExit,
                            colors = ButtonDefaults.buttonColors(
                                containerColor = Color(0xFFFF5252),
                                contentColor = Color.White
                            ),
                            shape = RoundedCornerShape(8.dp)
                        ) {
                            Text("Disconnect", fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
                        }
                    }
                }
            }
        }
    }
}