package com.h3h.s8cam

import java.io.File

/**
 * Lightweight CPU telemetry monitor:
 * - CPU usage percentage via /proc/stat deltas
 * - CPU core temperature via /sys/class/thermal
 */
class CpuMonitor {
    private var prevWork: Long = 0L
    private var prevTotal: Long = 0L
    private var thermalZonePath: String? = null

    init {
        thermalZonePath = findCpuThermalZone()
    }

    fun sampleUsage(): Double? {
        return try {
            val statFile = File("/proc/stat")
            if (!statFile.canRead()) return null
            val line = statFile.bufferedReader().use { it.readLine() } ?: return null
            if (!line.startsWith("cpu ")) return null
            val parts = line.trim().split("\\s+".toRegex())
            if (parts.size < 8) return null
            val user = parts[1].toLongOrNull() ?: 0L
            val nice = parts[2].toLongOrNull() ?: 0L
            val system = parts[3].toLongOrNull() ?: 0L
            val idle = parts[4].toLongOrNull() ?: 0L
            val iowait = parts[5].toLongOrNull() ?: 0L
            val irq = parts[6].toLongOrNull() ?: 0L
            val softirq = parts[7].toLongOrNull() ?: 0L
            val steal = if (parts.size > 8) parts[8].toLongOrNull() ?: 0L else 0L

            val work = user + nice + system + irq + softirq + steal
            val total = work + idle + iowait

            if (prevTotal == 0L) {
                prevWork = work
                prevTotal = total
                return null
            }

            val deltaWork = work - prevWork
            val deltaTotal = total - prevTotal

            prevWork = work
            prevTotal = total

            if (deltaTotal > 0) {
                ((deltaWork * 100.0) / deltaTotal).coerceIn(0.0, 100.0)
            } else null
        } catch (_: Exception) {
            null
        }
    }

    fun sampleTemperature(): Double? {
        val path = thermalZonePath ?: findCpuThermalZone().also { thermalZonePath = it } ?: return null
        return try {
            val file = File(path)
            if (!file.canRead()) return null
            val raw = file.readText().trim().toDoubleOrNull() ?: return null
            val temp = if (raw > 1000.0) raw / 1000.0 else raw
            if (temp in 0.0..120.0) temp else null
        } catch (_: Exception) {
            null
        }
    }

    private fun findCpuThermalZone(): String? {
        val thermalDir = File("/sys/class/thermal")
        if (thermalDir.exists() && thermalDir.isDirectory) {
            val zones = thermalDir.listFiles { f -> f.name.startsWith("thermal_zone") }
            if (zones != null) {
                val priorities = listOf("mngs", "apollo", "cpu", "soc", "tsens", "exynos")
                var bestPath: String? = null
                var bestPriority = Int.MAX_VALUE

                for (zone in zones) {
                    val tempFile = File(zone, "temp")
                    if (!tempFile.canRead()) continue
                    val typeFile = File(zone, "type")
                    val type = try {
                        if (typeFile.canRead()) typeFile.readText().trim().lowercase() else ""
                    } catch (_: Exception) { "" }

                    for ((index, keyword) in priorities.withIndex()) {
                        if (type.contains(keyword) && index < bestPriority) {
                            bestPriority = index
                            bestPath = tempFile.absolutePath
                            break
                        }
                    }
                }
                if (bestPath != null) return bestPath
            }
        }

        val fallback = File("/sys/class/thermal/thermal_zone0/temp")
        return if (fallback.canRead()) fallback.absolutePath else null
    }
}
