(() => {
    const messages = JSON.parse(document.getElementById('alertcenter-localization')?.textContent || '{}');

    if (!document.getElementById('oncall-group-form')) return;
    $('#GroupInput_TimeZoneId').select2({
        width: '100%',
        minimumResultsForSearch: 0,
        language: {
            noResults: () => (messages["No matching timezone found"] ?? "No matching timezone found")
        }
    });
})();
