// ==================== CONFIGURATION ====================
const CONFIG = {
    // Otomatik yeniləmə intervalı (ms)
    ROOM_REFRESH_INTERVAL: 5000,
    // Oyun timer (saniyə)
    TURN_TIMER: 30,
    // Debug mode
    DEBUG: true
};

// Log helper
function log(message, data = null) {
    if (CONFIG.DEBUG) {
        console.log(`[OKEY] ${message}`, data || '');
    }
}

// Error helper
function logError(message, error = null) {
    console.error(`[OKEY ERROR] ${message}`, error || '');
}