(function () {
    if (typeof window._uartDataReceived === 'undefined') return;

    console.log('[Adapter] Native bridge found, activating...');

    var port = navigator.serial._port;
    if (!port) return;

    port.onDataReceived = function (data) {
        console.log('[Adapter] Data:', data);
    };

    window._uartPort = port;

    // Перехватываем данные от native
    var originalHandler = window._uartDataReceived;
    window._uartDataReceived = function (data) {
        if (window._uartPort && window._uartPort.onDataReceived) {
            window._uartPort.onDataReceived(data);
        }
        if (originalHandler) originalHandler(data);
    };

    console.log('[Adapter] Ready');
})();