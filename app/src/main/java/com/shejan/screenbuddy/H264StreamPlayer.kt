package com.shejan.screenbuddy

import android.media.MediaCodec
import android.media.MediaFormat
import android.util.Log
import android.view.Surface
import java.io.IOException
import java.io.InputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.Socket
import java.nio.ByteBuffer

/**
 * Connects to the Windows ScreenBuddy companion app over TCP, reads H.264 packets
 * formatted according to our custom protocol, and decodes them directly to the provided Surface.
 * Authenticates itself using a 6-digit PIN handshake.
 */
class H264StreamPlayer(
    private val host: String,
    private val port: Int,
    private val pin: String,
    private val surface: Surface,
    private val callback: Callback
) {
    interface Callback {
        fun onConnected()
        fun onDisconnected()
        fun onError(e: Exception)
    }

    private var workerThread: Thread? = null
    @Volatile
    private var isRunning = false
    private var socket: Socket? = null
    private var codec: MediaCodec? = null

    companion object {
        private const val TAG = "H264StreamPlayer"
        private const val MSG_VIDEO_FRAME = 1
        private const val MSG_INPUT_EVENT = 2
        private const val MSG_HANDSHAKE   = 3
        private const val MSG_DISCONNECT  = 5
        private const val DEQUEUE_TIMEOUT_US = 5000L // 5ms
    }

    /**
     * Starts the player thread and begins streaming.
     */
    @Synchronized
    fun start() {
        if (isRunning) return
        isRunning = true
        workerThread = Thread { runDecodingLoop() }.apply {
            name = "H264StreamPlayerThread"
            start()
        }
    }

    /**
     * Stops decoding, disconnects the socket, and releases all MediaCodec resources.
     */
    @Synchronized
    fun stop() {
        if (!isRunning) return
        isRunning = false

        // Close socket first to interrupt any blocking read operations
        try {
            socket?.close()
        } catch (e: Exception) {
            Log.e(TAG, "Error closing socket: ${e.message}")
        }
        socket = null

        workerThread?.interrupt()
        workerThread = null

        // Stop and release MediaCodec
        try {
            codec?.stop()
            codec?.release()
        } catch (e: Exception) {
            Log.e(TAG, "Error releasing MediaCodec: ${e.message}")
        }
        codec = null
    }

    /**
     * Sends a touch/mouse input event to the stream server.
     * Format: [1 byte action] [4 bytes float X] [4 bytes float Y]
     */
    fun sendInputEvent(action: Int, normalizedX: Float, normalizedY: Float) {
        val payload = ByteBuffer.allocate(9).apply {
            put(action.toByte())
            putFloat(normalizedX)
            putFloat(normalizedY)
        }.array()
        sendPacket(MSG_INPUT_EVENT, payload)
    }

    @Synchronized
    private fun sendPacket(type: Int, payload: ByteArray) {
        val activeSocket = socket ?: return
        try {
            val outStream = activeSocket.getOutputStream()
            val header = ByteArray(8)

            // msgType (big endian)
            header[0] = ((type shr 24) and 0xff).toByte()
            header[1] = ((type shr 16) and 0xff).toByte()
            header[2] = ((type shr 8) and 0xff).toByte()
            header[3] = (type and 0xff).toByte()

            // length (big endian)
            val length = payload.size
            header[4] = ((length shr 24) and 0xff).toByte()
            header[5] = ((length shr 16) and 0xff).toByte()
            header[6] = ((length shr 8) and 0xff).toByte()
            header[7] = (length and 0xff).toByte()

            outStream.write(header)
            if (length > 0) {
                outStream.write(payload)
            }
            outStream.flush()
        } catch (e: Exception) {
            Log.e(TAG, "Error sending socket packet: ${e.message}", e)
        }
    }

    private fun runDecodingLoop() {
        try {
            Log.d(TAG, "Connecting to $host:$port...")
            val connSocket = Socket(host, port)
            socket = connSocket
            Log.d(TAG, "Connected to server. Sending secure handshake...")

            // 1. Send the PIN Verification handshake immediately
            val pinBytes = pin.toByteArray(Charsets.UTF_8)
            sendPacket(MSG_HANDSHAKE, pinBytes)
            
            // Notify UI
            callback.onConnected()

            val inputStream = connSocket.getInputStream()

            // Initialize hardware H.264 decoder
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, 1920, 1080).apply {
                setInteger(MediaFormat.KEY_LATENCY, 0)
            }
            val decoder = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            decoder.configure(format, surface, null, 0)
            decoder.start()
            codec = decoder

            val header = ByteArray(8)
            val bufferInfo = MediaCodec.BufferInfo()

            while (isRunning && !Thread.currentThread().isInterrupted) {
                // Read 8-byte header
                try {
                    readExact(inputStream, header, 8)
                } catch (e: IOException) {
                    if (isRunning) throw e
                    else break
                }

                // Parse Type
                val msgType = ((header[0].toInt() and 0xff) shl 24) or
                              ((header[1].toInt() and 0xff) shl 16) or
                              ((header[2].toInt() and 0xff) shl 8) or
                              (header[3].toInt() and 0xff)

                // Parse Length
                val payloadLen = ((header[4].toInt() and 0xff) shl 24) or
                                 ((header[5].toInt() and 0xff) shl 16) or
                                 ((header[6].toInt() and 0xff) shl 8) or
                                 (header[7].toInt() and 0xff)

                if (msgType == MSG_DISCONNECT) {
                    Log.d(TAG, "Disconnect message received from server.")
                    break
                }

                if (msgType == MSG_VIDEO_FRAME && payloadLen > 0) {
                    // Read H.264 NAL frame payload
                    val payload = ByteArray(payloadLen)
                    readExact(inputStream, payload, payloadLen)

                    // Queue raw bitstream frame into MediaCodec
                    val inputBufferIndex = decoder.dequeueInputBuffer(DEQUEUE_TIMEOUT_US)
                    if (inputBufferIndex >= 0) {
                        val inputBuffer: ByteBuffer? = decoder.getInputBuffer(inputBufferIndex)
                        if (inputBuffer != null) {
                            inputBuffer.clear()
                            inputBuffer.put(payload)
                            decoder.queueInputBuffer(
                                inputBufferIndex,
                                0,
                                payloadLen,
                                System.nanoTime() / 1000,
                                0
                            )
                        }
                    }

                    // Drain output buffers and render to Surface
                    var outputBufferIndex = decoder.dequeueOutputBuffer(bufferInfo, 0)
                    while (outputBufferIndex >= 0) {
                        decoder.releaseOutputBuffer(outputBufferIndex, true)
                        outputBufferIndex = decoder.dequeueOutputBuffer(bufferInfo, 0)
                    }
                }
            }

            Log.d(TAG, "Streaming session ended normally.")
            callback.onDisconnected()
        } catch (e: Exception) {
            if (isRunning) {
                Log.e(TAG, "Streaming error: ${e.message}", e)
                callback.onError(e)
            }
        } finally {
            stop()
        }
    }

    @Throws(IOException::class)
    private fun readExact(inputStream: java.io.InputStream, buffer: ByteArray, length: Int) {
        var bytesRead = 0
        while (bytesRead < length) {
            val count = inputStream.read(buffer, bytesRead, length - bytesRead)
            if (count < 0) {
                throw IOException("Socket input stream closed prematurely (read $bytesRead of $length bytes)")
            }
            bytesRead += count
        }
    }
}

/**
 * Handles secure auto-discovery of PC stream servers on the local WiFi network via UDP Broadcast.
 */
class PCDiscovery {
    interface Callback {
        fun onDiscovered(ip: String, port: Int)
        fun onDiscoveryFailed(e: Exception)
    }

    companion object {
        private const val TAG = "PCDiscovery"
        private const val DISCOVERY_PORT = 7891
        private const val TIMEOUT_MS     = 4000 // 4 seconds timeout

        fun discoverAndConnect(pin: String, callback: Callback) {
            Thread {
                var socket: DatagramSocket? = null
                try {
                    socket = DatagramSocket().apply {
                        broadcast = true
                        soTimeout = TIMEOUT_MS
                    }

                    // 1. Broadcast DISCOVER token over the local LAN
                    val requestMessage = "SCREENBUDDY_DISCOVER|$pin"
                    val requestBytes = requestMessage.toByteArray(Charsets.UTF_8)
                    val broadcastAddress = InetAddress.getByName("255.255.255.255")
                    
                    val sendPacket = DatagramPacket(
                        requestBytes,
                        requestBytes.size,
                        broadcastAddress,
                        DISCOVERY_PORT
                    )
                    socket.send(sendPacket)
                    Log.d(TAG, "Auto-discovery broadcast sent for PIN: $pin")

                    // 2. Wait for server unicast response
                    val buffer = ByteArray(1024)
                    val receivePacket = DatagramPacket(buffer, buffer.size)
                    socket.receive(receivePacket)

                    val response = String(receivePacket.data, 0, receivePacket.length, Charsets.UTF_8)
                    Log.d(TAG, "Auto-discovery response received: $response")

                    if (response.startsWith("SCREENBUDDY_OFFER|")) {
                        val serverPort = response.substring("SCREENBUDDY_OFFER|".length).toInt()
                        val serverIp = receivePacket.address.hostAddress ?: ""
                        callback.onDiscovered(serverIp, serverPort)
                    } else {
                        callback.onDiscoveryFailed(IOException("Invalid response signature from server."))
                    }
                } catch (e: Exception) {
                    Log.e(TAG, "Auto-discovery failed: ${e.message}")
                    callback.onDiscoveryFailed(e)
                } finally {
                    socket?.close()
                }
            }.start()
        }
    }
}
