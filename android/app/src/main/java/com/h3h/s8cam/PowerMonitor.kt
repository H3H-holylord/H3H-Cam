package com.h3h.s8cam

import android.content.Context
import android.content.Intent
import android.os.BatteryManager
import android.os.Build
import org.json.JSONObject
import kotlin.math.abs

/** Standard Android battery data only. Unsupported values remain JSON null. */
class PowerMonitor(context: Context) {
    private val manager = context.getSystemService(BatteryManager::class.java)

    fun sample(intent: Intent?): JSONObject {
        val statusCode = intent?.getIntExtra(BatteryManager.EXTRA_STATUS, BatteryManager.BATTERY_STATUS_UNKNOWN)
            ?: BatteryManager.BATTERY_STATUS_UNKNOWN
        val pluggedCode = intent?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0
        val level = intent?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
        val scale = intent?.getIntExtra(BatteryManager.EXTRA_SCALE, -1) ?: -1
        val intentPercent = if (level >= 0 && scale > 0) (level * 100f / scale).toInt().coerceIn(0, 100) else null

        // Direct hardware reads to bypass any frozen/mocked ADB battery states
        val directCapacity = intProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)?.takeIf { it in 0..100 }
        val sysfsCapacity = readSysfsCapacity()
        val percent = sysfsCapacity ?: directCapacity ?: intentPercent

        val isPhysicallyPlugged = pluggedCode != 0 || readSysfsPlugged()

        val voltageMv = intent?.getIntExtra(BatteryManager.EXTRA_VOLTAGE, Int.MIN_VALUE)
            ?.takeUnless { it == Int.MIN_VALUE || it <= 0 }
            ?: readSysfsVoltageMv()
        val temperatureTenths = intent?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, Int.MIN_VALUE)
            ?.takeUnless { it == Int.MIN_VALUE }
            ?: readSysfsTempTenths()
        val rawCurrentUa = intProperty(BatteryManager.BATTERY_PROPERTY_CURRENT_NOW) ?: readSysfsCurrentUa()
        val averageCurrentUa = intProperty(BatteryManager.BATTERY_PROPERTY_CURRENT_AVERAGE)
        val normalizedCurrentUa = rawCurrentUa?.let { normalizeCurrent(it, statusCode) }
        val chargeCounterUah = intProperty(BatteryManager.BATTERY_PROPERTY_CHARGE_COUNTER)?.takeIf { it > 0 }
        val energyCounterNwh = longProperty(BatteryManager.BATTERY_PROPERTY_ENERGY_COUNTER)?.takeIf { it > 0 }
        val chargeTimeMs = if (Build.VERSION.SDK_INT >= 28) manager.computeChargeTimeRemaining().takeIf { it >= 0 } else null

        return JSONObject()
            .putNullable("batteryPercent", percent)
            .put("batteryStatus", statusName(statusCode))
            .put("batteryCharging", statusCode == BatteryManager.BATTERY_STATUS_CHARGING || statusCode == BatteryManager.BATTERY_STATUS_FULL)
            .put("batterySource", if (isPhysicallyPlugged && pluggedCode == 0) "USB" else sourceName(pluggedCode))
            .putNullable("batteryVoltageMv", voltageMv)
            .putNullable("batteryCurrentUa", normalizedCurrentUa)
            .putNullable("batteryCurrentRawUa", rawCurrentUa)
            .putNullable("batteryCurrentAverageUa", averageCurrentUa?.let { normalizeCurrent(it, statusCode) })
            .put("batteryCurrentSignNormalized", rawCurrentUa != null && normalizedCurrentUa != rawCurrentUa)
            .putNullable("batteryPowerMw", if (voltageMv != null && normalizedCurrentUa != null)
                voltageMv.toDouble() * normalizedCurrentUa / 1_000_000.0 else null)
            .putNullable("batteryChargeCounterUah", chargeCounterUah)
            .putNullable("batteryEnergyCounterNwh", energyCounterNwh)
            .putNullable("batteryTemperatureC", temperatureTenths?.div(10.0))
            .put("batteryHealth", healthName(intent?.getIntExtra(BatteryManager.EXTRA_HEALTH, 0) ?: 0))
            .putNullable("batteryTechnology", intent?.getStringExtra(BatteryManager.EXTRA_TECHNOLOGY)?.takeIf { it.isNotBlank() })
            .putNullable("batteryChargeTimeRemainingMs", chargeTimeMs)
    }

    private fun intProperty(id: Int): Int? = try {
        manager.getIntProperty(id).takeUnless { it == Int.MIN_VALUE }
    } catch (_: Exception) { null }

    private fun longProperty(id: Int): Long? = try {
        manager.getLongProperty(id).takeUnless { it == Long.MIN_VALUE }
    } catch (_: Exception) { null }

    // Android documents CURRENT_NOW in µA. Standard Linux battery driver:
    // Positive = charging (flowing into battery).
    // Negative = discharging (flowing out of battery).
    // Note: If plugged into USB and value is negative, it means NET DISCHARGE (device consumes more than USB delivers)!
    // We only orient the sign if on pure battery (discharging) the driver erroneously reports positive.
    private fun normalizeCurrent(value: Int, status: Int): Int = when (status) {
        BatteryManager.BATTERY_STATUS_CHARGING, BatteryManager.BATTERY_STATUS_FULL -> value
        BatteryManager.BATTERY_STATUS_DISCHARGING -> if (value > 0) -value else value
        else -> value
    }

    private fun statusName(value: Int) = when (value) {
        BatteryManager.BATTERY_STATUS_CHARGING -> "Charging"
        BatteryManager.BATTERY_STATUS_DISCHARGING -> "Discharging"
        BatteryManager.BATTERY_STATUS_FULL -> "Full"
        BatteryManager.BATTERY_STATUS_NOT_CHARGING -> "Not charging"
        else -> "Unknown"
    }

    private fun sourceName(value: Int) = when (value) {
        BatteryManager.BATTERY_PLUGGED_USB -> "USB"
        BatteryManager.BATTERY_PLUGGED_AC -> "AC"
        BatteryManager.BATTERY_PLUGGED_WIRELESS -> "Wireless"
        else -> "Battery"
    }

    private fun healthName(value: Int) = when (value) {
        BatteryManager.BATTERY_HEALTH_GOOD -> "Good"
        BatteryManager.BATTERY_HEALTH_OVERHEAT -> "Overheat"
        BatteryManager.BATTERY_HEALTH_DEAD -> "Dead"
        BatteryManager.BATTERY_HEALTH_OVER_VOLTAGE -> "Over voltage"
        BatteryManager.BATTERY_HEALTH_UNSPECIFIED_FAILURE -> "Failure"
        BatteryManager.BATTERY_HEALTH_COLD -> "Cold"
        else -> "Unknown"
    }

    private fun JSONObject.putNullable(name: String, value: Any?): JSONObject =
        put(name, value ?: JSONObject.NULL)

    private fun readSysfsCapacity(): Int? = try {
        listOf(
            "/sys/class/power_supply/battery/capacity",
            "/sys/class/power_supply/battery/batt_soc",
            "/sys/class/power_supply/battery/real_soc"
        ).firstNotNullOfOrNull { path ->
            val f = java.io.File(path)
            if (f.exists() && f.canRead()) f.readText().trim().toIntOrNull()?.coerceIn(0, 100) else null
        }
    } catch (_: Exception) { null }

    private fun readSysfsPlugged(): Boolean = try {
        listOf(
            "/sys/class/power_supply/usb/online",
            "/sys/class/power_supply/battery/charging_enabled",
            "/sys/class/power_supply/sec-charger/online"
        ).any { path ->
            val f = java.io.File(path)
            f.exists() && f.canRead() && (f.readText().trim() == "1")
        }
    } catch (_: Exception) { false }

    private fun readSysfsVoltageMv(): Int? = try {
        listOf(
            "/sys/class/power_supply/battery/voltage_now",
            "/sys/class/power_supply/battery/batt_vol"
        ).firstNotNullOfOrNull { path ->
            val f = java.io.File(path)
            if (f.exists() && f.canRead()) {
                val v = f.readText().trim().toIntOrNull()
                if (v != null && v > 0) {
                    if (v > 100_000) v / 1000 else v
                } else null
            } else null
        }
    } catch (_: Exception) { null }

    private fun readSysfsTempTenths(): Int? = try {
        listOf(
            "/sys/class/power_supply/battery/temp",
            "/sys/class/power_supply/battery/batt_temp"
        ).firstNotNullOfOrNull { path ->
            val f = java.io.File(path)
            if (f.exists() && f.canRead()) {
                val t = f.readText().trim().toIntOrNull()
                if (t != null) {
                    if (t > 1000) t / 100 else t
                } else null
            } else null
        }
    } catch (_: Exception) { null }

    private fun readSysfsCurrentUa(): Int? = try {
        listOf(
            "/sys/class/power_supply/battery/current_now",
            "/sys/class/power_supply/battery/batt_current"
        ).firstNotNullOfOrNull { path ->
            val f = java.io.File(path)
            if (f.exists() && f.canRead()) f.readText().trim().toIntOrNull() else null
        }
    } catch (_: Exception) { null }
}
