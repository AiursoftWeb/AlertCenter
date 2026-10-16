document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('[data-open-on-load="true"]').forEach(dialog => {
        if (dialog.dataset.noticeInitialized) return;
        dialog.dataset.noticeInitialized = 'true';
        dialog.addEventListener('hidden.bs.modal', () => dialog.previousElementSibling.focus());
        bootstrap.Modal.getOrCreateInstance(dialog).show();
    });
});
