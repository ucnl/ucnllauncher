// device-adapter.js — Адаптер для связи WebView с нативным USB
// v2 — работает совместно с webview-stub.js, не переопределяет navigator.serial
(function () {
    console.log('[Adapter] Native bridge found, activating...');

    // Не переопределяем navigator.serial — webview-stub.js уже сделал это
    // Только логируем для диагностики
    if (navigator.serial && navigator.serial.requestPort) {
        console.log('[Adapter] navigator.serial already available (webview-stub)');
    } else {
        console.log('[Adapter] WARNING: navigator.serial not found!');
    }

    // Для обратной совместимости со старыми приложениями
    window._uartDataReceived = function (data) {
        console.log('[Adapter] Native data: ' + (data ? data.length : 0) + ' bytes');
    };

    console.log('[Adapter] Ready');
})();