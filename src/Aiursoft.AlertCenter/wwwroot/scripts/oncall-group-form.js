(() => {
    if (!document.getElementById('oncall-group-form')) return;
    $('#GroupInput_TimeZoneId').select2({
        width: '100%',
        minimumResultsForSearch: 0,
        language: {
            noResults: () => '未找到匹配的时区'
        }
    });
})();
