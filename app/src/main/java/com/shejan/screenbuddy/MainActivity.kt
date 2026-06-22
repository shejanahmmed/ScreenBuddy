package com.shejan.screenbuddy

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.SharedPreferences
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.os.Bundle
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ExperimentalGetImage
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.animation.*
import androidx.compose.animation.core.*
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.*
import androidx.compose.material.icons.filled.*
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import com.google.android.gms.ads.AdRequest
import com.google.android.gms.ads.AdSize
import com.google.android.gms.ads.AdView
import com.google.android.gms.ads.MobileAds
import com.google.android.gms.ads.rewarded.RewardedAd
import com.google.android.gms.ads.rewarded.RewardedAdLoadCallback
import com.google.android.gms.ads.LoadAdError
import com.google.android.gms.ads.FullScreenContentCallback
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import com.shejan.screenbuddy.ui.theme.ScreenBuddyTheme
import kotlinx.coroutines.delay
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException

// ─────────────────────────────────────────────────────────────────────────────
// DATA TYPES AND STATE ROUTER
// ─────────────────────────────────────────────────────────────────────────────

sealed class Screen {
    object Splash : Screen()
    object Onboarding : Screen()
    object Home : Screen()
    object QrScanner : Screen()
    data class ActiveDisplay(val ip: String, val port: Int, val pin: String) : Screen()
    object History : Screen()
    object Settings : Screen()
}

data class HistoryItem(
    val id: String,
    val name: String,
    val ip: String,
    val port: Int,
    val timestamp: Long
)

data class DiscoveredPC(
    val ip: String,
    val port: Int,
    val name: String = "Windows PC"
)

// ─────────────────────────────────────────────────────────────────────────────
// PREFERENCE AND STORAGE UTILITY
// ─────────────────────────────────────────────────────────────────────────────

class PreferenceManager(context: Context) {
    private val prefs: SharedPreferences = context.getSharedPreferences("screenbuddy_prefs", Context.MODE_PRIVATE)

    fun isFirstLaunch(): Boolean = prefs.getBoolean("first_launch", true)
    fun setFirstLaunchComplete() = prefs.edit().putBoolean("first_launch", false).apply()

    fun getStreamQuality(): String = prefs.getString("stream_quality", "Auto") ?: "Auto"
    fun setStreamQuality(quality: String) = prefs.edit().putString("stream_quality", quality).apply()

    fun getBitrateSlider(): Float = prefs.getFloat("bitrate_slider", 2000f)
    fun setBitrateSlider(value: Float) = prefs.edit().putFloat("bitrate_slider", value).apply()

    fun getKeepAwake(): Boolean = prefs.getBoolean("keep_awake", true)
    fun setKeepAwake(value: Boolean) = prefs.edit().putBoolean("keep_awake", value).apply()

    fun getAutoReconnect(): Boolean = prefs.getBoolean("auto_reconnect", false)
    fun setAutoReconnect(value: Boolean) = prefs.edit().putBoolean("auto_reconnect", value).apply()

    fun getAppTheme(): String = prefs.getString("app_theme", "System") ?: "System"
    fun setAppTheme(theme: String) = prefs.edit().putString("app_theme", theme).apply()

    fun hasRemovedAdsForSession(): Boolean = prefs.getBoolean("ads_removed_session", false)
    fun setAdsRemovedForSession(value: Boolean) = prefs.edit().putBoolean("ads_removed_session", value).apply()

    fun getHistory(): List<HistoryItem> {
        val json = prefs.getString("history_json", "[]") ?: "[]"
        val list = mutableListOf<HistoryItem>()
        try {
            val arr = JSONArray(json)
            for (i in 0 until arr.length()) {
                val obj = arr.getJSONObject(i)
                list.add(
                    HistoryItem(
                        id = obj.getString("id"),
                        name = obj.getString("name"),
                        ip = obj.getString("ip"),
                        port = obj.getInt("port"),
                        timestamp = obj.getLong("timestamp")
                    )
                )
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
        return list
    }

    fun addHistoryItem(name: String, ip: String, port: Int) {
        val current = getHistory().toMutableList()
        current.removeAll { it.ip == ip && it.port == port }
        current.add(0, HistoryItem(
            id = java.util.UUID.randomUUID().toString(),
            name = name,
            ip = ip,
            port = port,
            timestamp = System.currentTimeMillis()
        ))
        if (current.size > 10) {
            current.removeAt(current.size - 1)
        }
        saveHistory(current)
    }

    fun deleteHistoryItem(id: String) {
        val current = getHistory().toMutableList()
        current.removeAll { it.id == id }
        saveHistory(current)
    }

    fun clearHistory() {
        saveHistory(emptyList())
    }

    private fun saveHistory(list: List<HistoryItem>) {
        val arr = JSONArray()
        for (item in list) {
            val obj = JSONObject().apply {
                put("id", item.id)
                put("name", item.name)
                put("ip", item.ip)
                put("port", item.port)
                put("timestamp", item.timestamp)
            }
            arr.put(obj)
        }
        prefs.edit().putString("history_json", arr.toString()).apply()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MAIN ACTIVITY INTERFACE
// ─────────────────────────────────────────────────────────────────────────────

class MainActivity : ComponentActivity() {

    private var player: H264StreamPlayer? = null
    private var rewardedAd: RewardedAd? = null
    private var isAdLoading = false
    private lateinit var prefManager: PreferenceManager

    // App state observables
    private var isConnected = mutableStateOf(false)
    private var isConnecting = mutableStateOf(false)
    private var connectionError = mutableStateOf<String?>(null)
    private var videoWidth = mutableStateOf(16)
    private var videoHeight = mutableStateOf(9)

    // Navigation and discovery states
    private var currentScreen = mutableStateOf<Screen>(Screen.Splash)
    private val discoveredPCs = mutableStateListOf<DiscoveredPC>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        prefManager = PreferenceManager(applicationContext)

        // Keep screen awake during streaming
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        // Initialize Ads SDK
        MobileAds.initialize(this) {}
        loadRewardedAd()

        setContent {
            val themeMode by remember { mutableStateOf(prefManager.getAppTheme()) }

            val darkTheme = when (themeMode) {
                "Dark" -> true
                "Light" -> false
                else -> isSystemInDarkTheme()
            }

            ScreenBuddyTheme(darkTheme = darkTheme) {
                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.background
                ) {
                    AppNavigationRouter(prefManager)
                }
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        stopMirroring()
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // CONNECTION CONTROLLERS
    // ─────────────────────────────────────────────────────────────────────────────

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
                    prefManager.addHistoryItem("Windows PC (${host})", host, port)
                }
            }

            override fun onDisconnected() {
                runOnUiThread {
                    isConnecting.value = false
                    isConnected.value = false
                    if (currentScreen.value is Screen.ActiveDisplay) {
                        currentScreen.value = Screen.Home
                    }
                }
            }

            override fun onError(e: Exception) {
                runOnUiThread {
                    isConnecting.value = false
                    isConnected.value = false
                    connectionError.value = e.message ?: "Connection error"
                    currentScreen.value = Screen.Home
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

    // ─────────────────────────────────────────────────────────────────────────────
    // REWARDED ADS HANDLERS
    // ─────────────────────────────────────────────────────────────────────────────

    private fun loadRewardedAd() {
        if (rewardedAd != null || isAdLoading) return
        isAdLoading = true
        val adRequest = AdRequest.Builder().build()
        RewardedAd.load(
            this,
            "ca-app-pub-3940256099942544/5224354917", // Test ad unit
            adRequest,
            object : RewardedAdLoadCallback() {
                override fun onAdFailedToLoad(adError: LoadAdError) {
                    rewardedAd = null
                    isAdLoading = false
                }

                override fun onAdLoaded(ad: RewardedAd) {
                    rewardedAd = ad
                    isAdLoading = false
                }
            }
        )
    }

    fun showRewardedAd(onRewardEarned: () -> Unit) {
        val ad = rewardedAd
        if (ad != null) {
            ad.fullScreenContentCallback = object : FullScreenContentCallback() {
                override fun onAdDismissedFullScreenContent() {
                    rewardedAd = null
                    loadRewardedAd()
                }
            }
            ad.show(this) {
                onRewardEarned()
            }
        } else {
            Toast.makeText(this, "Ad loading failed. Granting access directly.", Toast.LENGTH_SHORT).show()
            onRewardEarned()
            loadRewardedAd()
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // NAVIGATION ROUTER
    // ─────────────────────────────────────────────────────────────────────────────

    @OptIn(ExperimentalAnimationApi::class)
    @Composable
    fun AppNavigationRouter(prefManager: PreferenceManager) {
        val screenState by currentScreen

        AnimatedContent(
            targetState = screenState,
            transitionSpec = {
                fadeIn(animationSpec = tween(300)) togetherWith fadeOut(animationSpec = tween(300))
            },
            label = "ScreenTransition"
        ) { targetScreen ->
            when (targetScreen) {
                is Screen.Splash -> SplashScreen(prefManager)
                is Screen.Onboarding -> OnboardingScreen(prefManager)
                is Screen.Home -> HomeScreen(prefManager)
                is Screen.QrScanner -> QrScannerScreen(prefManager)
                is Screen.ActiveDisplay -> ActiveDisplayScreen(
                    ip = targetScreen.ip,
                    port = targetScreen.port,
                    pin = targetScreen.pin,
                    prefManager = prefManager
                )
                is Screen.History -> HistoryScreen(prefManager)
                is Screen.Settings -> SettingsScreen(prefManager)
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCREEN 1: SPLASH SCREEN
    // ─────────────────────────────────────────────────────────────────────────────

    @Composable
    fun SplashScreen(prefManager: PreferenceManager) {
        var startAnimation by remember { mutableStateOf(false) }
        val scale by animateFloatAsState(
            targetValue = if (startAnimation) 1f else 0.5f,
            animationSpec = tween(800, easing = LinearOutSlowInEasing),
            label = "SplashScale"
        )
        val alpha by animateFloatAsState(
            targetValue = if (startAnimation) 1f else 0f,
            animationSpec = tween(800),
            label = "SplashAlpha"
        )

        LaunchedEffect(Unit) {
            startAnimation = true
            delay(1200)

            // Auto-reconnect checks
            val history = prefManager.getHistory()
            if (prefManager.getAutoReconnect() && history.isNotEmpty()) {
                val lastItem = history.first()
                isConnecting.value = true
                isConnected.value = false
                currentScreen.value = Screen.ActiveDisplay(lastItem.ip, lastItem.port, "000000")
            } else if (prefManager.isFirstLaunch()) {
                currentScreen.value = Screen.Onboarding
            } else {
                currentScreen.value = Screen.Home
            }
        }

        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(
                    Brush.verticalGradient(
                        colors = listOf(Color(0xFF0F2027), Color(0xFF203A43), Color(0xFF2C5364))
                    )
                ),
            contentAlignment = Alignment.Center
        ) {
            Column(
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Box(
                    modifier = Modifier
                        .size(100.dp)
                        .clip(CircleShape)
                        .background(Color.White.copy(alpha = 0.1f))
                        .blur(1.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        imageVector = Icons.Default.Monitor,
                        contentDescription = null,
                        tint = Color(0xFF00ADB5),
                        modifier = Modifier
                            .size(60.dp)
                            .scale(scale)
                    )
                }
                Spacer(modifier = Modifier.height(20.dp))
                Text(
                    text = "ScreenBuddy",
                    fontSize = 28.sp,
                    fontWeight = FontWeight.Bold,
                    color = Color.White,
                    modifier = Modifier.scale(scale)
                )
                Text(
                    text = "Local WiFi Screen Mirroring",
                    fontSize = 14.sp,
                    color = Color.White.copy(alpha = 0.6f),
                    modifier = Modifier.scale(scale)
                )
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCREEN 2: ONBOARDING SCREEN
    // ─────────────────────────────────────────────────────────────────────────────

    @Composable
    fun OnboardingScreen(prefManager: PreferenceManager) {
        var slideIndex by remember { mutableIntStateOf(0) }

        val onboardingSlides = listOf(
            Triple(
                Icons.Default.Devices,
                "Mirror Instantly",
                "Stream your Windows PC screen directly onto your mobile or tablet with hardware accelerated speed."
            ),
            Triple(
                Icons.Default.WifiLock,
                "Completely Local",
                "Your video streams and touch inputs remain entirely on your local WiFi. No cloud relays, no tracking."
            ),
            Triple(
                Icons.Default.TouchApp,
                "Bi-directional Controls",
                "Control your Windows desktop by tapping and dragging your fingers. Swipe from the edge to configure settings."
            )
        )

        val currentSlide = onboardingSlides[slideIndex]

        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color(0xFF0F172A))
                .padding(24.dp)
        ) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .align(Alignment.Center),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                Box(
                    modifier = Modifier
                        .size(140.dp)
                        .clip(RoundedCornerShape(24.dp))
                        .background(Color.White.copy(alpha = 0.05f))
                        .padding(24.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        imageVector = currentSlide.first,
                        contentDescription = null,
                        tint = Color(0xFF00ADB5),
                        modifier = Modifier.size(80.dp)
                    )
                }

                Spacer(modifier = Modifier.height(40.dp))

                Text(
                    text = currentSlide.second,
                    fontSize = 24.sp,
                    fontWeight = FontWeight.Bold,
                    color = Color.White,
                    textAlign = TextAlign.Center
                )

                Spacer(modifier = Modifier.height(16.dp))

                Text(
                    text = currentSlide.third,
                    fontSize = 15.sp,
                    color = Color.LightGray,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.padding(horizontal = 16.dp)
                )

                Spacer(modifier = Modifier.height(40.dp))

                // Privacy Indicator Card
                if (slideIndex == 1) {
                    Card(
                        colors = CardDefaults.cardColors(
                            containerColor = Color(0xFF00ADB5).copy(alpha = 0.1f)
                        ),
                        border = BorderStroke(1.dp, Color(0xFF00ADB5).copy(alpha = 0.3f)),
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Row(
                            modifier = Modifier.padding(16.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Icon(
                                imageVector = Icons.Default.Shield,
                                contentDescription = null,
                                tint = Color(0xFF00ADB5),
                                modifier = Modifier.size(24.dp)
                            )
                            Spacer(modifier = Modifier.width(12.dp))
                            Text(
                                text = "Everything stays on your local WiFi — nothing is uploaded",
                                color = Color.White,
                                fontSize = 13.sp
                            )
                        }
                    }
                }
            }

            // Bottom Actions Layer
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .align(Alignment.BottomCenter)
                    .padding(bottom = 16.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "Skip",
                    color = Color.White.copy(alpha = 0.6f),
                    modifier = Modifier
                        .clickable {
                            prefManager.setFirstLaunchComplete()
                            currentScreen.value = Screen.Home
                        }
                        .padding(8.dp)
                )

                // Page indicator dots
                Row {
                    onboardingSlides.forEachIndexed { i, _ ->
                        Box(
                            modifier = Modifier
                                .padding(4.dp)
                                .size(if (i == slideIndex) 10.dp else 6.dp)
                                .clip(CircleShape)
                                .background(if (i == slideIndex) Color(0xFF00ADB5) else Color.Gray)
                        )
                    }
                }

                Button(
                    onClick = {
                        if (slideIndex < onboardingSlides.size - 1) {
                            slideIndex++
                        } else {
                            prefManager.setFirstLaunchComplete()
                            currentScreen.value = Screen.Home
                        }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF00ADB5))
                ) {
                    Text(if (slideIndex == onboardingSlides.size - 1) "Get Started" else "Next")
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCREEN 3: HOME / PAIRING SCREEN
    // ─────────────────────────────────────────────────────────────────────────────

    @Composable
    fun HomeScreen(prefManager: PreferenceManager) {
        var pairingPin by remember { mutableStateOf("") }
        var isDiscovering by remember { mutableStateOf(false) }

        val cameraPermissionLauncher = rememberLauncherForActivityResult(
            contract = ActivityResultContracts.RequestPermission()
        ) { isGranted ->
            if (isGranted) {
                currentScreen.value = Screen.QrScanner
            } else {
                Toast.makeText(this, "Camera permission required to scan QR.", Toast.LENGTH_SHORT).show()
            }
        }

        // Periodically run network discovery in the background if 6 digit pin is entered
        LaunchedEffect(pairingPin) {
            if (pairingPin.length == 6) {
                isDiscovering = true
                discoveredPCs.clear()
                PCDiscovery.discoverAndConnect(pairingPin, object : PCDiscovery.Callback {
                    override fun onDiscovered(ip: String, port: Int) {
                        runOnUiThread {
                            isDiscovering = false
                            if (discoveredPCs.none { it.ip == ip }) {
                                discoveredPCs.add(DiscoveredPC(ip, port))
                            }
                        }
                    }

                    override fun onDiscoveryFailed(e: Exception) {
                        runOnUiThread {
                            isDiscovering = false
                        }
                    }
                })
            } else {
                discoveredPCs.clear()
            }
        }

        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color(0xFF0F172A))
        ) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .statusBarsPadding()
                    .padding(horizontal = 24.dp, vertical = 16.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                // Header Nav Icons
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 8.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    IconButton(onClick = { currentScreen.value = Screen.History }) {
                        Icon(
                            imageVector = Icons.Default.History,
                            contentDescription = "History",
                            tint = Color.White
                        )
                    }
                    Text(
                        text = "ScreenBuddy",
                        fontWeight = FontWeight.Bold,
                        color = Color.White,
                        fontSize = 20.sp
                    )
                    IconButton(onClick = { currentScreen.value = Screen.Settings }) {
                        Icon(
                            imageVector = Icons.Default.Settings,
                            contentDescription = "Settings",
                            tint = Color.White
                        )
                    }
                }

                Spacer(modifier = Modifier.height(20.dp))

                // QR Scan and Code Container Card
                Card(
                    shape = RoundedCornerShape(16.dp),
                    colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.05f)),
                    border = BorderStroke(1.dp, Color.White.copy(alpha = 0.1f)),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(24.dp),
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        // QR Scan Trigger
                        Box(
                            modifier = Modifier
                                .size(70.dp)
                                .clip(CircleShape)
                                .background(Color(0xFF00ADB5))
                                .clickable {
                                    val permissionCheck = ContextCompat.checkSelfPermission(
                                        this@MainActivity,
                                        Manifest.permission.CAMERA
                                    )
                                    if (permissionCheck == PackageManager.PERMISSION_GRANTED) {
                                        currentScreen.value = Screen.QrScanner
                                    } else {
                                        cameraPermissionLauncher.launch(Manifest.permission.CAMERA)
                                    }
                                },
                            contentAlignment = Alignment.Center
                        ) {
                            Icon(
                                imageVector = Icons.Default.QrCodeScanner,
                                contentDescription = "Scan QR",
                                tint = Color.White,
                                modifier = Modifier.size(36.dp)
                            )
                        }

                        Spacer(modifier = Modifier.height(12.dp))

                        Text(
                            text = "Scan PC Companion QR",
                            fontWeight = FontWeight.SemiBold,
                            color = Color.White,
                            fontSize = 16.sp
                        )

                        Spacer(modifier = Modifier.height(24.dp))

                        Text(
                            text = "Or Enter pairing code manually",
                            color = Color.White.copy(alpha = 0.6f),
                            fontSize = 13.sp
                        )

                        Spacer(modifier = Modifier.height(12.dp))

                        OutlinedTextField(
                            value = pairingPin,
                            onValueChange = {
                                if (it.length <= 6 && it.all { char -> char.isDigit() }) {
                                    pairingPin = it
                                }
                            },
                            placeholder = {
                                Text(
                                    text = "6-Digit PIN",
                                    modifier = Modifier.fillMaxWidth(),
                                    textAlign = TextAlign.Center,
                                    style = LocalTextStyle.current.copy(
                                        color = Color.Gray,
                                        textAlign = TextAlign.Center,
                                        fontWeight = FontWeight.Bold,
                                        fontSize = 18.sp
                                    )
                                )
                            },
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            singleLine = true,
                            textStyle = LocalTextStyle.current.copy(
                                color = Color.White,
                                textAlign = TextAlign.Center,
                                fontWeight = FontWeight.Bold,
                                fontSize = 18.sp
                            ),
                            colors = OutlinedTextFieldDefaults.colors(
                                focusedBorderColor = Color(0xFF00ADB5),
                                unfocusedBorderColor = Color.White.copy(alpha = 0.3f),
                                cursorColor = Color(0xFF00ADB5)
                            ),
                            modifier = Modifier.fillMaxWidth(0.8f)
                        )
                    }
                }

                Spacer(modifier = Modifier.height(30.dp))

                // Discovered PCs Section
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f)
                ) {
                    Text(
                        text = "Discovered PCs on network",
                        fontWeight = FontWeight.SemiBold,
                        color = Color.White,
                        fontSize = 14.sp,
                        modifier = Modifier.align(Alignment.CenterHorizontally)
                    )

                    Spacer(modifier = Modifier.height(10.dp))

                    if (pairingPin.length < 6) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .weight(1f),
                            contentAlignment = Alignment.Center
                        ) {
                            Text(
                                text = "Enter the 6-digit pairing PIN shown on your PC companion app to search local devices.",
                                color = Color.White.copy(alpha = 0.4f),
                                fontSize = 13.sp,
                                textAlign = TextAlign.Center,
                                modifier = Modifier.padding(horizontal = 24.dp)
                            )
                        }
                    } else if (isDiscovering && discoveredPCs.isEmpty()) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .weight(1f),
                            contentAlignment = Alignment.Center
                        ) {
                            CircularProgressIndicator(color = Color(0xFF00ADB5))
                        }
                    } else if (discoveredPCs.isEmpty()) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .weight(1f),
                            contentAlignment = Alignment.Center
                        ) {
                            Text(
                                text = "Searching local network... Check server PIN.",
                                color = Color.White.copy(alpha = 0.4f),
                                fontSize = 13.sp,
                                textAlign = TextAlign.Center,
                                modifier = Modifier.padding(horizontal = 24.dp)
                            )
                        }
                    } else {
                        LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                            items(discoveredPCs) { pc ->
                                Card(
                                    colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.08f)),
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clickable {
                                            isConnecting.value = true
                                            isConnected.value = false
                                            currentScreen.value = Screen.ActiveDisplay(pc.ip, pc.port, pairingPin)
                                        }
                                ) {
                                    Row(
                                        modifier = Modifier.padding(16.dp),
                                        verticalAlignment = Alignment.CenterVertically
                                    ) {
                                        Icon(
                                            imageVector = Icons.Default.Computer,
                                            contentDescription = null,
                                            tint = Color(0xFF00ADB5)
                                        )
                                        Spacer(modifier = Modifier.width(16.dp))
                                        Column {
                                            Text(pc.name, color = Color.White, fontWeight = FontWeight.Medium)
                                            Text("${pc.ip}:${pc.port}", color = Color.LightGray, fontSize = 12.sp)
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // AdMob Banner Section
                AdMobBanner(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(50.dp),
                    preferenceManager = prefManager
                )
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCREEN 4: QR CODE SCANNER VIEW
    // ─────────────────────────────────────────────────────────────────────────────

    @Composable
    fun QrScannerScreen(prefManager: PreferenceManager) {
        val context = LocalContext.current
        val lifecycleOwner = LocalLifecycleOwner.current
        val cameraProviderFuture = remember { ProcessCameraProvider.getInstance(context) }
        val cameraProviderState = remember { mutableStateOf<ProcessCameraProvider?>(null) }

        LaunchedEffect(cameraProviderFuture) {
            cameraProviderFuture.addListener({
                cameraProviderState.value = cameraProviderFuture.get()
            }, ContextCompat.getMainExecutor(context))
        }

        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black)
        ) {
            val provider = cameraProviderState.value
            if (provider != null) {
                AndroidView(
                    factory = { ctx ->
                        PreviewView(ctx).apply {
                            scaleType = PreviewView.ScaleType.FILL_CENTER
                        }
                    },
                    modifier = Modifier.fillMaxSize(),
                    update = { previewView ->
                        val preview = Preview.Builder().build().apply {
                            setSurfaceProvider(previewView.surfaceProvider)
                        }

                        val imageAnalysis = ImageAnalysis.Builder()
                            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                            .build()
                            .apply {
                                setAnalyzer(
                                    ContextCompat.getMainExecutor(context),
                                    BarcodeAnalyser { qrText ->
                                        provider.unbindAll()
                                        // Format: screenbuddy://connect?ip=192.168.1.1&port=7890&pin=123456
                                        try {
                                            val uri = android.net.Uri.parse(qrText)
                                            val ip = uri.getQueryParameter("ip")
                                            val port = uri.getQueryParameter("port")?.toIntOrNull()
                                            val pin = uri.getQueryParameter("pin")

                                            if (ip != null && port != null && pin != null) {
                                                isConnecting.value = true
                                                isConnected.value = false
                                                currentScreen.value = Screen.ActiveDisplay(ip, port, pin)
                                            } else {
                                                Toast.makeText(context, "Invalid QR Format.", Toast.LENGTH_SHORT).show()
                                                currentScreen.value = Screen.Home
                                            }
                                        } catch (e: Exception) {
                                            currentScreen.value = Screen.Home
                                        }
                                    }
                                )
                            }

                        val cameraSelector = CameraSelector.DEFAULT_BACK_CAMERA

                        try {
                            provider.unbindAll()
                            provider.bindToLifecycle(
                                lifecycleOwner,
                                cameraSelector,
                                preview,
                                imageAnalysis
                            )
                        } catch (e: Exception) {
                            e.printStackTrace()
                        }
                    }
                )
            } else {
                Box(
                    modifier = Modifier.fillMaxSize(),
                    contentAlignment = Alignment.Center
                ) {
                    CircularProgressIndicator(color = Color(0xFF00ADB5))
                }
            }

            // Scanner overlay targets
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(24.dp)
            ) {
                // Bracket visual targets
                Box(
                    modifier = Modifier
                        .size(260.dp)
                        .border(BorderStroke(2.dp, Color(0xFF00ADB5)), shape = RoundedCornerShape(12.dp))
                        .align(Alignment.Center)
                )

                // Fallback link at bottom
                Button(
                    onClick = {
                        provider?.unbindAll()
                        currentScreen.value = Screen.Home
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = Color.White.copy(alpha = 0.2f)),
                    modifier = Modifier
                        .align(Alignment.BottomCenter)
                        .padding(bottom = 40.dp)
                ) {
                    Text("Enter Code Manually", color = Color.White)
                }
            }
        }
    }

    // Barcode analyzer inside CameraX
    private class BarcodeAnalyser(private val onQrScanned: (String) -> Unit) : ImageAnalysis.Analyzer {
        private val scanner = BarcodeScanning.getClient(
            BarcodeScannerOptions.Builder()
                .setBarcodeFormats(Barcode.FORMAT_QR_CODE)
                .build()
        )

        @ExperimentalGetImage
        override fun analyze(imageProxy: ImageProxy) {
            val mediaImage = imageProxy.image
            if (mediaImage != null) {
                val image = InputImage.fromMediaImage(mediaImage, imageProxy.imageInfo.rotationDegrees)
                scanner.process(image)
                    .addOnSuccessListener { barcodes ->
                        for (barcode in barcodes) {
                            barcode.rawValue?.let { qrText ->
                                if (qrText.startsWith("screenbuddy://")) {
                                    onQrScanned(qrText)
                                }
                            }
                        }
                    }
                    .addOnCompleteListener {
                        imageProxy.close()
                    }
            } else {
                imageProxy.close()
            }
        }
    }



    // ─────────────────────────────────────────────────────────────────────────────
    // SCREEN 6: ACTIVE DISPLAY SCREEN
    // ─────────────────────────────────────────────────────────────────────────────

    @Composable
    fun ActiveDisplayScreen(ip: String, port: Int, pin: String, prefManager: PreferenceManager) {
        var showOverlay by remember { mutableStateOf(true) }
        var orientationMode by remember { mutableStateOf("Auto") }
        var muteEnabled by remember { mutableStateOf(false) }

        val connecting by isConnecting
        val connected by isConnected

        // Pulsating connection animation for loading overlay
        val infiniteTransition = rememberInfiniteTransition(label = "ConnectingTransition")
        val scale by infiniteTransition.animateFloat(
            initialValue = 0.8f,
            targetValue = 1.2f,
            animationSpec = infiniteRepeatable(
                animation = tween(1000, easing = LinearEasing),
                repeatMode = RepeatMode.Reverse
            ),
            label = "ConnectingScale"
        )

        // Local Activity Orientation lock
        val activity = LocalContext.current as? Activity
        LaunchedEffect(orientationMode) {
            activity?.requestedOrientation = when (orientationMode) {
                "Portrait" -> ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
                "Landscape" -> ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
                else -> ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
            }
        }

        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color.Black)
        ) {
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
                                startMirroring(ip, port, pin, holder.surface)
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

            // Connection Loading Overlay
            if (connecting || !connected) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(Color(0xFF0F172A)),
                    contentAlignment = Alignment.Center
                ) {
                    Column(
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Center,
                        modifier = Modifier.padding(24.dp)
                    ) {
                        Box(
                            modifier = Modifier
                                .size(100.dp)
                                .scale(scale)
                                .clip(CircleShape)
                                .background(Color(0xFF00ADB5).copy(alpha = 0.1f)),
                            contentAlignment = Alignment.Center
                        ) {
                            CircularProgressIndicator(
                                color = Color(0xFF00ADB5),
                                strokeWidth = 3.dp,
                                modifier = Modifier.size(50.dp)
                            )
                        }

                        Spacer(modifier = Modifier.height(40.dp))

                        Text(
                            text = "Connecting to companion server...",
                            fontSize = 18.sp,
                            fontWeight = FontWeight.Bold,
                            color = Color.White
                        )

                        Text(
                            text = "Target Address: $ip:$port",
                            fontSize = 13.sp,
                            color = Color.White.copy(alpha = 0.6f),
                            modifier = Modifier.padding(top = 8.dp)
                        )

                        Spacer(modifier = Modifier.height(40.dp))

                        Button(
                            onClick = {
                                stopMirroring()
                                currentScreen.value = Screen.Home
                            },
                            colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFFF5252))
                        ) {
                            Text("Cancel", color = Color.White)
                        }
                    }
                }
            }

            // Dynamic Overlay Panel overlay card
            AnimatedVisibility(
                visible = showOverlay,
                enter = fadeIn(),
                exit = fadeOut(),
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
            ) {
                Card(
                    shape = RoundedCornerShape(topStart = 16.dp, topEnd = 16.dp),
                    colors = CardDefaults.cardColors(containerColor = Color.Black.copy(alpha = 0.85f)),
                    modifier = Modifier.wrapContentHeight()
                ) {
                    Column(modifier = Modifier.padding(16.dp)) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Column {
                                Text("Streaming mirror active", color = Color.White, fontWeight = FontWeight.Bold)
                                Text("Double tap to toggle controls card", color = Color.Gray, fontSize = 11.sp)
                            }

                            Button(
                                onClick = {
                                    stopMirroring()
                                    currentScreen.value = Screen.Home
                                },
                                colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFFF5252))
                            ) {
                                Text("Disconnect", fontSize = 12.sp)
                            }
                        }

                        HorizontalDivider(
                            modifier = Modifier.padding(vertical = 12.dp),
                            color = Color.White.copy(alpha = 0.2f)
                        )

                        // Settings sliders inside active overlay
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceAround
                        ) {
                            // Mute toggle
                            IconButton(onClick = { muteEnabled = !muteEnabled }) {
                                Icon(
                                    imageVector = if (muteEnabled) Icons.AutoMirrored.Filled.VolumeOff else Icons.AutoMirrored.Filled.VolumeUp,
                                    contentDescription = "Mute",
                                    tint = if (muteEnabled) Color(0xFFFF5252) else Color(0xFF00ADB5)
                                )
                            }

                            // Quality shortcut toggle
                            TextButton(onClick = {
                                val currentQuality = prefManager.getStreamQuality()
                                val next = when (currentQuality) {
                                    "Low" -> "Medium"
                                    "Medium" -> "High"
                                    else -> "Low"
                                }
                                prefManager.setStreamQuality(next)
                            }) {
                                Text("Quality: ${prefManager.getStreamQuality()}", color = Color.White)
                            }

                            // Orientation locking toggle
                            IconButton(onClick = {
                                orientationMode = when (orientationMode) {
                                    "Auto" -> "Landscape"
                                    "Landscape" -> "Portrait"
                                    else -> "Auto"
                                }
                            }) {
                                Icon(
                                    imageVector = when (orientationMode) {
                                        "Portrait" -> Icons.Default.StayCurrentPortrait
                                        "Landscape" -> Icons.Default.StayCurrentLandscape
                                        else -> Icons.Default.ScreenRotation
                                    },
                                    contentDescription = "Orientation Mode",
                                    tint = Color(0xFF00ADB5)
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCREEN 7: CONNECTION HISTORY
    // ─────────────────────────────────────────────────────────────────────────────

    @Composable
    fun HistoryScreen(prefManager: PreferenceManager) {
        val historyList = remember { mutableStateListOf<HistoryItem>().apply { addAll(prefManager.getHistory()) } }

        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color(0xFF0F172A))
        ) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .statusBarsPadding()
                    .padding(horizontal = 24.dp, vertical = 16.dp)
            ) {
                // Header Nav
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    IconButton(onClick = { currentScreen.value = Screen.Home }) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                            contentDescription = "Back",
                            tint = Color.White
                        )
                    }
                    Spacer(modifier = Modifier.width(16.dp))
                    Text(
                        text = "Connection History",
                        fontWeight = FontWeight.Bold,
                        color = Color.White,
                        fontSize = 20.sp
                    )
                }

                Spacer(modifier = Modifier.height(16.dp))

                if (historyList.isEmpty()) {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .weight(1f),
                        contentAlignment = Alignment.Center
                    ) {
                        Text("No past connections recorded.", color = Color.Gray, fontSize = 14.sp)
                    }
                } else {
                    LazyColumn(
                        modifier = Modifier.weight(1f),
                        verticalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        items(historyList, key = { it.id }) { item ->
                            Card(
                                colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.05f)),
                                border = BorderStroke(1.dp, Color.White.copy(alpha = 0.1f))
                            ) {
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clickable {
                                            isConnecting.value = true
                                            isConnected.value = false
                                            currentScreen.value = Screen.ActiveDisplay(item.ip, item.port, "000000")
                                        }
                                        .padding(16.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.SpaceBetween
                                ) {
                                    Column(modifier = Modifier.weight(1f)) {
                                        Text(item.name, color = Color.White, fontWeight = FontWeight.Medium)
                                        Text(
                                            text = "${item.ip}:${item.port}",
                                            color = Color.LightGray,
                                            fontSize = 12.sp
                                        )
                                    }

                                    IconButton(
                                        onClick = {
                                            prefManager.deleteHistoryItem(item.id)
                                            historyList.remove(item)
                                        }
                                    ) {
                                        Icon(
                                            imageVector = Icons.Default.Delete,
                                            contentDescription = "Delete",
                                            tint = Color(0xFFFF5252)
                                        )
                                    }
                                }
                            }
                        }
                    }
                }

                // AdMob Banner
                AdMobBanner(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(50.dp),
                    preferenceManager = prefManager
                )
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SCREEN 8: SETTINGS SCREEN
    // ─────────────────────────────────────────────────────────────────────────────

    @Composable
    fun SettingsScreen(prefManager: PreferenceManager) {
        var appTheme by remember { mutableStateOf(prefManager.getAppTheme()) }
        var streamQuality by remember { mutableStateOf(prefManager.getStreamQuality()) }
        var bitrateSlider by remember { mutableFloatStateOf(prefManager.getBitrateSlider()) }
        var keepAwake by remember { mutableStateOf(prefManager.getKeepAwake()) }
        var autoReconnect by remember { mutableStateOf(prefManager.getAutoReconnect()) }
        var adsRemoved by remember { mutableStateOf(prefManager.hasRemovedAdsForSession()) }

        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(Color(0xFF0F172A))
        ) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .statusBarsPadding()
                    .padding(horizontal = 24.dp, vertical = 16.dp)
            ) {
                // Header Nav
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    IconButton(onClick = { currentScreen.value = Screen.Home }) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                            contentDescription = "Back",
                            tint = Color.White
                        )
                    }
                    Spacer(modifier = Modifier.width(16.dp))
                    Text(
                        text = "Settings",
                        fontWeight = FontWeight.Bold,
                        color = Color.White,
                        fontSize = 20.sp
                    )
                }

                Spacer(modifier = Modifier.height(16.dp))

                LazyColumn(
                    modifier = Modifier.weight(1f),
                    verticalArrangement = Arrangement.spacedBy(16.dp)
                ) {
                    // Group 1: Stream Quality Setting
                    item {
                        SettingsGroupCard(title = "Stream Quality Settings") {
                            Column {
                                Text("Quality Mode", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 14.sp)
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween
                                ) {
                                    listOf("Auto", "Low", "Medium", "High").forEach { q ->
                                        FilterChip(
                                            selected = streamQuality == q,
                                            onClick = {
                                                streamQuality = q
                                                prefManager.setStreamQuality(q)
                                            },
                                            label = { Text(q) }
                                        )
                                    }
                                }

                                Spacer(modifier = Modifier.height(16.dp))

                                Text("Manual Bitrate Limit (${bitrateSlider.toInt()} kbps)", color = Color.White, fontSize = 14.sp)
                                Slider(
                                    value = bitrateSlider,
                                    onValueChange = {
                                        bitrateSlider = it
                                        prefManager.setBitrateSlider(it)
                                    },
                                    valueRange = 500f..10000f,
                                    colors = SliderDefaults.colors(
                                        thumbColor = Color(0xFF00ADB5),
                                        activeTrackColor = Color(0xFF00ADB5)
                                    )
                                )
                            }
                        }
                    }

                    // Group 2: Display & Awake
                    item {
                        SettingsGroupCard(title = "Display Settings") {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Text(
                                    text = "Keep screen awake while connected",
                                    color = Color.White,
                                    fontSize = 14.sp,
                                    modifier = Modifier.weight(1f)
                                )
                                Spacer(modifier = Modifier.width(16.dp))
                                PremiumSwitch(
                                    checked = keepAwake,
                                    onCheckedChange = {
                                        keepAwake = it
                                        prefManager.setKeepAwake(it)
                                    }
                                )
                            }
                        }
                    }

                    // Group 3: Reconnection / Setup parameters
                    item {
                        SettingsGroupCard(title = "Connection Settings") {
                            Column {
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween,
                                    verticalAlignment = Alignment.CenterVertically
                                ) {
                                    Text(
                                        text = "Auto-reconnect to last PC on launch",
                                        color = Color.White,
                                        fontSize = 14.sp,
                                        modifier = Modifier.weight(1f)
                                    )
                                    Spacer(modifier = Modifier.width(16.dp))
                                    PremiumSwitch(
                                        checked = autoReconnect,
                                        onCheckedChange = {
                                            autoReconnect = it
                                            prefManager.setAutoReconnect(it)
                                        }
                                    )
                                }

                                Spacer(modifier = Modifier.height(12.dp))

                                Button(
                                    onClick = {
                                        prefManager.clearHistory()
                                        Toast.makeText(this@MainActivity, "Forget all saved PCs", Toast.LENGTH_SHORT).show()
                                    },
                                    colors = ButtonDefaults.buttonColors(containerColor = Color.White.copy(alpha = 0.1f)),
                                    modifier = Modifier.fillMaxWidth()
                                ) {
                                    Text("Forget Saved PCs", color = Color(0xFFFF5252))
                                }
                            }
                        }
                    }

                    // Group 4: Premium and Ads Remove Actions
                    item {
                        SettingsGroupCard(title = "Ads Management") {
                            Column {
                                Text(
                                    text = if (adsRemoved) "Ads removed for this session!" else "Banner ads are currently enabled.",
                                    color = Color.White.copy(alpha = 0.6f),
                                    fontSize = 13.sp
                                )

                                Spacer(modifier = Modifier.height(12.dp))

                                Button(
                                    onClick = {
                                        showRewardedAd {
                                            prefManager.setAdsRemovedForSession(true)
                                            adsRemoved = true
                                            Toast.makeText(this@MainActivity, "Premium mode unlocked! Ads removed.", Toast.LENGTH_LONG).show()
                                        }
                                    },
                                    enabled = !adsRemoved,
                                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF00ADB5)),
                                    modifier = Modifier.fillMaxWidth()
                                ) {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        Icon(imageVector = Icons.Default.PlayCircle, contentDescription = null)
                                        Spacer(modifier = Modifier.width(8.dp))
                                        Text("Watch Video to Remove Ads")
                                    }
                                }
                            }
                        }
                    }

                    // Group 5: Appearance customization
                    item {
                        SettingsGroupCard(title = "Appearance Theme") {
                            Row(
                                modifier = Modifier.fillMaxWidth(),
                                horizontalArrangement = Arrangement.SpaceBetween
                            ) {
                                listOf("Light", "Dark", "System").forEach { theme ->
                                    FilterChip(
                                        selected = appTheme == theme,
                                        onClick = {
                                            appTheme = theme
                                            prefManager.setAppTheme(theme)
                                        },
                                        label = { Text(theme) }
                                    )
                                }
                            }
                        }
                    }

                    // Group 6: About App version specs
                    item {
                        SettingsGroupCard(title = "About") {
                            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    horizontalArrangement = Arrangement.SpaceBetween
                                ) {
                                    Text("App version", color = Color.White, fontSize = 14.sp)
                                    Text("1.0.0 (Stable)", color = Color.LightGray, fontSize = 14.sp)
                                }
                                Text(
                                    text = "Privacy Policy & Terms of Use",
                                    color = Color(0xFF00ADB5),
                                    fontSize = 13.sp,
                                    modifier = Modifier.clickable {
                                        Toast.makeText(this@MainActivity, "Opens Policy Link...", Toast.LENGTH_SHORT).show()
                                    }
                                )
                            }
                        }
                    }
                }
            }
        }
    }

    @Composable
    private fun SettingsGroupCard(title: String, content: @Composable () -> Unit) {
        Card(
            shape = RoundedCornerShape(12.dp),
            colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.05f)),
            border = BorderStroke(1.dp, Color.White.copy(alpha = 0.1f)),
            modifier = Modifier.fillMaxWidth()
        ) {
            Column(modifier = Modifier.padding(16.dp)) {
                Text(
                    text = title,
                    fontWeight = FontWeight.Bold,
                    fontSize = 15.sp,
                    color = Color(0xFF00ADB5),
                    modifier = Modifier.padding(bottom = 12.dp)
                )
                content()
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CUSTOM ADMOB AD CONTAINER
// ─────────────────────────────────────────────────────────────────────────────

@Composable
fun AdMobBanner(modifier: Modifier = Modifier, preferenceManager: PreferenceManager) {
    if (preferenceManager.hasRemovedAdsForSession()) {
        Spacer(modifier = Modifier.height(0.dp))
        return
    }

    AndroidView(
        modifier = modifier,
        factory = { context ->
            AdView(context).apply {
                setAdSize(AdSize.BANNER)
                adUnitId = "ca-app-pub-3940256099942544/6300978111" // Google Play Services Test ad unit ID
                loadAd(AdRequest.Builder().build())
            }
        }
    )
}

// ─────────────────────────────────────────────────────────────────────────────
// PREMIUM CUSTOM SWITCH
// ─────────────────────────────────────────────────────────────────────────────

@Composable
fun PremiumSwitch(
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier
) {
    val trackWidth = 46.dp
    val trackHeight = 26.dp
    val thumbSize = 20.dp
    val padding = 3.dp

    // Animate the thumb offset
    val thumbOffset by animateDpAsState(
        targetValue = if (checked) trackWidth - thumbSize - padding else padding,
        animationSpec = spring(stiffness = Spring.StiffnessMedium),
        label = "ThumbOffset"
    )

    // Animate colors
    val trackColor by animateColorAsState(
        targetValue = if (checked) Color(0xFF00ADB5) else Color.White.copy(alpha = 0.15f),
        animationSpec = tween(200),
        label = "TrackColor"
    )
    val thumbColor by animateColorAsState(
        targetValue = if (checked) Color.White else Color.Gray,
        animationSpec = tween(200),
        label = "ThumbColor"
    )

    Box(
        modifier = modifier
            .size(width = trackWidth, height = trackHeight)
            .clip(RoundedCornerShape(100))
            .background(trackColor)
            .clickable { onCheckedChange(!checked) },
        contentAlignment = Alignment.CenterStart
    ) {
        Box(
            modifier = Modifier
                .offset(x = thumbOffset)
                .size(thumbSize)
                .clip(CircleShape)
                .background(thumbColor)
        )
    }
}