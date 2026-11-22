(() => {
    'use strict';
    const form = document.getElementById('group-reset-form');
    if (!form) return;

    const field = name => form.elements.namedItem(name);
    const includeManual = field('includeManual');
    const clear = document.getElementById('group-reset-clear');
    const regenerate = document.getElementById('group-reset-regenerate');
    const confirm = document.getElementById('group-reset-confirm');
    const summary = document.getElementById('group-reset-summary');
    const errors = document.getElementById('group-reset-errors');
    const canRegenerate = regenerate.dataset.unavailable !== 'true';
    let approved = null;
    let busy = false;

    const invalidate = () => {
        approved = null;
        summary.hidden = true;
        confirm.hidden = true;
        confirm.disabled = true;
    };
    const showErrors = messages => {
        errors.querySelector('.alert-message').replaceChildren(...messages.map(message => {
            const line = document.createElement('div');
            line.textContent = message;
            return line;
        }));
        errors.hidden = !messages.length;
    };
    const setBusy = value => {
        busy = value;
        clear.disabled = value;
        regenerate.disabled = value || !canRegenerate;
        includeManual.disabled = value;
        confirm.disabled = value || !approved;
    };

    async function reset(isPreview, regenerateShifts = approved?.regenerate) {
        if (busy || (!isPreview && !approved) || (regenerateShifts && !canRegenerate)) return;
        const selection = { regenerate: regenerateShifts, includeManual: includeManual.checked };
        if (!isPreview && selection.includeManual !== approved.includeManual) {
            invalidate();
            return;
        }
        const version = isPreview ? form.dataset.version : approved.version;
        invalidate();
        setBusy(true);
        showErrors([]);
        let reloading = false;
        try {
            const body = new URLSearchParams({ id: field('id').value, ...selection, preview: isPreview, version });
            const response = await fetch(form.action, {
                method: 'POST', credentials: 'same-origin',
                headers: { RequestVerificationToken: field('__RequestVerificationToken').value }, body
            });
            const data = await response.json().catch(() => null);
            const messages = Array.isArray(data?.errors) ? data.errors : [];
            if (!response.ok || messages.length) {
                showErrors(messages.length ? messages : [form.dataset.failed]);
                return;
            }
            if (!isPreview) {
                window.location.reload();
                reloading = true;
                return;
            }
            if (!data?.version || !Number.isInteger(data.deletedCount) || !Number.isInteger(data.generatedCount)) {
                showErrors([form.dataset.failed]);
                return;
            }
            const formatter = new Intl.DateTimeFormat('en-GB', {
                timeZone: form.dataset.timeZone, year: 'numeric', month: '2-digit', day: '2-digit',
                hour: '2-digit', minute: '2-digit', hourCycle: 'h23'
            });
            const formatTime = utc => {
                if (utc == null) return form.dataset.noRange;
                const parts = Object.fromEntries(formatter.formatToParts(new Date(utc)).map(({ type, value }) => [type, value]));
                return `${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}`;
            };
            for (const name of ['deletedCount', 'generatedCount']) {
                summary.querySelector(`[data-result="${name}"]`).textContent = data[name];
            }
            for (const name of ['startUtc', 'endUtc']) {
                summary.querySelector(`[data-result="${name}"]`).textContent = formatTime(data[name]);
            }
            approved = { ...selection, version: data.version };
            summary.hidden = false;
            confirm.textContent = regenerateShifts ? confirm.dataset.regenerate : confirm.dataset.clear;
            confirm.hidden = false;
        } catch {
            showErrors([form.dataset.failed]);
        } finally {
            if (!reloading) setBusy(false);
        }
    }

    includeManual.addEventListener('change', () => {
        invalidate();
        showErrors([]);
    });
    form.addEventListener('submit', event => event.preventDefault());
    clear.addEventListener('click', () => reset(true, false));
    regenerate.addEventListener('click', () => reset(true, true));
    confirm.addEventListener('click', () => reset(false));
    setBusy(false);
})();
