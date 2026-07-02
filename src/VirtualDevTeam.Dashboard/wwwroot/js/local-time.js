(function () {
    function parseBool(value, fallback) {
        if (value === undefined || value === null || value === '') {
            return fallback;
        }

        return value === true || value === 'true';
    }

    function isSameLocalDay(left, right) {
        return left.getFullYear() === right.getFullYear()
            && left.getMonth() === right.getMonth()
            && left.getDate() === right.getDate();
    }

    function formatPart(date, options) {
        return new Intl.DateTimeFormat(undefined, options).format(date);
    }

    function getFormattedText(date, element) {
        var includeSeconds = parseBool(element.dataset.includeSeconds, false);
        var includeDate = parseBool(element.dataset.includeDate, false);
        var includeDateWhenNotToday = parseBool(element.dataset.includeDateWhenNotToday, true);
        var includeYearWhenNotCurrentYear = parseBool(element.dataset.includeYearWhenNotCurrentYear, true);
        var now = new Date();
        var includeDateForRender = includeDate || (includeDateWhenNotToday && !isSameLocalDay(date, now));

        var timeOptions = {
            hour: 'numeric',
            minute: '2-digit',
            hour12: true
        };

        if (includeSeconds) {
            timeOptions.second = '2-digit';
        }

        var formattedTime = formatPart(date, timeOptions);
        if (!includeDateForRender) {
            return formattedTime;
        }

        var dateOptions = {
            month: 'short',
            day: 'numeric'
        };

        if (includeYearWhenNotCurrentYear && date.getFullYear() !== now.getFullYear()) {
            dateOptions.year = 'numeric';
        }

        return formatPart(date, dateOptions) + ', ' + formattedTime;
    }

    function getTooltipText(date) {
        return new Intl.DateTimeFormat(undefined, {
            month: 'short',
            day: 'numeric',
            year: 'numeric',
            hour: 'numeric',
            minute: '2-digit',
            second: '2-digit',
            hour12: true,
            timeZoneName: 'short'
        }).format(date);
    }

    function formatElement(element) {
        if (!(element instanceof HTMLElement)) {
            return;
        }

        var utcValue = element.dataset.utc;
        if (!utcValue) {
            return;
        }

        var date = new Date(utcValue);
        if (Number.isNaN(date.getTime())) {
            return;
        }

        element.textContent = getFormattedText(date, element);
        element.setAttribute('datetime', date.toISOString());
        if (!element.hasAttribute('title')) {
            element.title = getTooltipText(date);
        }
    }

    function formatRoot(root) {
        if (!root) {
            return;
        }

        if (root instanceof HTMLElement && root.matches('[data-local-time="true"]')) {
            formatElement(root);
        }

        if (typeof root.querySelectorAll !== 'function') {
            return;
        }

        root.querySelectorAll('[data-local-time="true"]').forEach(formatElement);
    }

    function startObserver() {
        formatRoot(document);

        if (!document.body || window.__virtualDevTeamLocalTimeObserver) {
            return;
        }

        var observer = new MutationObserver(function (mutations) {
            mutations.forEach(function (mutation) {
                mutation.addedNodes.forEach(function (node) {
                    if (node.nodeType === Node.ELEMENT_NODE) {
                        formatRoot(node);
                    }
                });
            });
        });

        observer.observe(document.body, { childList: true, subtree: true });
        window.__virtualDevTeamLocalTimeObserver = observer;
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', startObserver, { once: true });
    } else {
        startObserver();
    }

    window.LocalTime = {
        refresh: function () { formatRoot(document); },
        formatElement: formatElement
    };
})();
