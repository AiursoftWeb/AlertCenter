(() => {
    'use strict';

    document.addEventListener('DOMContentLoaded', () => {
        const url = new URL(window.location.href);
        if (url.searchParams.get('ack') !== 'true') return;

        // Consume the action before posting so refresh/back cannot repeat it.
        url.searchParams.delete('ack');
        window.history.replaceState(window.history.state, '', url);
        const form = document.getElementById('acknowledge-incident');
        form?.requestSubmit();
    });
})();
