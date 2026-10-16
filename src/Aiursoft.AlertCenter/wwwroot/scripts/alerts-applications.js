(() => {
(() => {
        const modal = document.getElementById('reset-api-key');
        const form = document.getElementById('reset-api-key-form');
        const applicationId = document.getElementById('reset-api-key-id');
        const applicationName = document.getElementById('reset-api-key-name');
        const submit = document.getElementById('confirm-reset-api-key');
        modal.addEventListener('show.bs.modal', event => {
            if (!event.relatedTarget) return;
            applicationId.value = event.relatedTarget.dataset.applicationId ?? '';
            applicationName.textContent = event.relatedTarget.dataset.applicationName ?? '';
            submit.disabled = !applicationId.value;
        });
        form.addEventListener('submit', event => {
            if (!applicationId.value || submit.disabled) {
                event.preventDefault();
                return;
            }
            if (!window.fetch || !window.bootstrap) submit.disabled = true;
        });
    })();
})();

(() => {
(() => {

        const modal = document.getElementById('delete-application');
        const form = document.getElementById('delete-application-form');
        const applicationId = document.getElementById('delete-application-id');
        const applicationName = document.getElementById('delete-application-name');
        const confirmation = document.getElementById('delete-application-confirmation');
        const submit = document.getElementById('confirm-delete-application');
        const updateConfirmation = () => {
            const matches = Boolean(applicationId.value) && confirmation.value.length > 0 &&
                confirmation.value === applicationName.textContent;
            confirmation.setCustomValidity(matches ? '' : getLocalizedText("enter-the-exact-app-name-to-confirm-deletion", "Enter the exact app name to confirm deletion."));
            submit.disabled = !matches;
            return matches;
        };
        modal.addEventListener('show.bs.modal', event => {
            if (!event.relatedTarget) return;
            applicationId.value = event.relatedTarget.dataset.applicationId ?? '';
            applicationName.textContent = event.relatedTarget.dataset.applicationName ?? '';
            confirmation.value = '';
            updateConfirmation();
        });
        modal.addEventListener('shown.bs.modal', () => confirmation.focus());
        confirmation.addEventListener('input', updateConfirmation);
        form.addEventListener('submit', event => {
            if (!updateConfirmation()) {
                event.preventDefault();
                confirmation.reportValidity();
            }
        });
    })();
})();

(() => {
document.getElementById('copy-key')?.addEventListener('click', async function () {
            const input = document.getElementById('api-key');
            try {
                await navigator.clipboard.writeText(input.value);
                document.getElementById('copy-status').textContent = getLocalizedText("successfully-copied-to-clipboard", "Successfully copied to clipboard!");
            } catch {
                input.select();
                document.getElementById('copy-status').textContent = getLocalizedText("press-ctrl-c-to-copy-the-selected-key", "Press Ctrl+C to copy the selected key.");
            }
        });
})();
