(() => {

    const form = document.getElementById('oncall-group-form');
    if (!form) return;
    function revealField(field) {
        const section = field.closest('details');
        if (section) section.open = true;
    }
    form.addEventListener('invalid', event => revealField(event.target), true);
    form.addEventListener('submit', () => {
        form.querySelectorAll(':invalid').forEach(revealField);
    }, true);
    $('#GroupInput_TimeZoneId').select2({
        width: '100%',
        minimumResultsForSearch: 0,
        language: {
            noResults: () => getLocalizedText("no-matching-timezone-found", "No matching timezone found")
        }
    });
})();
