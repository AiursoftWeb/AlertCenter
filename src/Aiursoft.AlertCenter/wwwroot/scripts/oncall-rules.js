(() => {
document.getElementById('delete-oncall-rule').addEventListener('show.bs.modal', function (event) {
                if (!event.relatedTarget) return;
                const id = event.relatedTarget?.dataset.ruleId ?? '';
                document.getElementById('delete-oncall-rule-id').value = id;
                document.getElementById('delete-oncall-rule-name').textContent = event.relatedTarget?.dataset.ruleName ?? '';
                document.getElementById('delete-oncall-rule-confirm').disabled = !id;
            });
})();
