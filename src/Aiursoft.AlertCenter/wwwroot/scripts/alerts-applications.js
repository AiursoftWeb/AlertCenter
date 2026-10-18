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
