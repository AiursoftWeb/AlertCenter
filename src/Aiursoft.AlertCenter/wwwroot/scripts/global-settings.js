(() => {
    document.querySelectorAll('[data-submit-on-change]').forEach(field => {
        field.addEventListener('change', () => field.form.submit());
    });
})();
