(function () {
    'use strict';

    window.BrowserNotifications = {
        /**
         * Request permission for browser notifications.
         * Returns: 'granted', 'denied', or 'default'
         */
        requestPermission: async function () {
            if (!('Notification' in window)) return 'unsupported';
            if (Notification.permission === 'granted') return 'granted';
            if (Notification.permission === 'denied') return 'denied';
            return await Notification.requestPermission();
        },

        /**
         * Show a browser notification.
         * @param {string} title - Notification title
         * @param {string} body - Notification body text
         * @param {string} icon - Optional icon URL
         * @param {string} url - Optional URL to open on click
         */
        show: function (title, body, icon, url) {
            if (!('Notification' in window) || Notification.permission !== 'granted') return;

            const notification = new Notification(title, {
                body: body,
                icon: icon || '/favicon.ico',
                badge: '/favicon.ico',
                tag: 'virtualdevteam-gate-' + Date.now(),
                requireInteraction: true
            });

            if (url) {
                notification.onclick = function () {
                    window.focus();
                    if (url.startsWith('http')) {
                        window.open(url, '_blank');
                    }
                    notification.close();
                };
            }

            // Auto-close after 30 seconds
            setTimeout(() => notification.close(), 30000);
        },

        /**
         * Check if notifications are supported and permitted.
         */
        isEnabled: function () {
            return 'Notification' in window && Notification.permission === 'granted';
        }
    };
})();
