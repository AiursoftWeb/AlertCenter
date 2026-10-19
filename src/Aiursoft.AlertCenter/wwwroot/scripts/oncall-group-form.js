(() => {

    const form = document.getElementById('oncall-group-form');
    if (!form) return;
    form.addEventListener('invalid', event => {
        const section = event.target.closest('details');
        if (section) section.open = true;
    }, true);
    $('#GroupInput_TimeZoneId').select2({
        width: '100%',
        minimumResultsForSearch: 0,
        language: {
            noResults: () => getLocalizedText("no-matching-timezone-found", "No matching timezone found")
        }
    });
})();
