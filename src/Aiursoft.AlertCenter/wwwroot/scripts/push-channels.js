(() => {
document.getElementById('test-channel').addEventListener('show.bs.modal', function (event) {
        if (!event.relatedTarget) return;
        document.getElementById('test-channel-name').textContent = event.relatedTarget?.dataset.channelName ?? '';
        document.getElementById('test-channel-id').value = event.relatedTarget?.dataset.channelId ?? '';
        const content = document.getElementById('test-channel-content');
        content.value = content.defaultValue;
        document.getElementById('test-channel-confirm').disabled = !event.relatedTarget?.dataset.channelId;
    });

    document.getElementById('delete-channel').addEventListener('show.bs.modal', function (event) {
        if (!event.relatedTarget) return;
        document.getElementById('delete-channel-name').textContent = event.relatedTarget?.dataset.channelName ?? '';
        document.getElementById('delete-channel-id').value = event.relatedTarget?.dataset.channelId ?? '';
        document.getElementById('delete-channel-confirm').disabled = !event.relatedTarget?.dataset.channelId;
    });
})();
