(() => {
    const form = document.getElementById('oncall-group-form');
    if (!form) return;
    function revealField(field) {
        for (let section = field.closest('details'); section; section = section.parentElement?.closest('details')) section.open = true;
    }
    form.addEventListener('invalid', event => revealField(event.target), true);
    form.addEventListener('submit', () => form.querySelectorAll(':invalid').forEach(revealField), true);

    const template = document.getElementById('GroupInput_RuleId');
    const zone = document.getElementById('GroupInput_TimeZoneId');
    const anchor = document.getElementById('GroupInput_AnchorLocal');
    const manualInput = document.getElementById('oncall-anchor-manual');
    const autoExtend = document.getElementById('oncall-autoextend');
    const notice = document.getElementById('oncall-anchor-notice');
    const suggestions = document.getElementById('oncall-anchor-suggestions');
    const preview = document.getElementById('oncall-group-preview');
    const status = document.getElementById('oncall-preview-status');
    const warnings = document.getElementById('oncall-preview-warnings');
    const selected = document.getElementById('oncall-selected');
    let manual = manualInput.value === 'true', sequence = 0;
    let needsSuggestion = !manual && !anchor.value;
    const localText = value => value.replace('T', ' ');
    const format = (text, value) => text.replace('{0}', value);

    function updateAutoExtend() {
        autoExtend.disabled = !template.value;
        if (autoExtend.disabled) { autoExtend.checked = false; notice.hidden = true; }
    }
    function markManual() {
        manual = true; needsSuggestion = false; manualInput.value = 'true'; notice.hidden = true;
    }
    function showSuggestions(values) {
        suggestions.replaceChildren();
        if (anchor.readOnly) return;
        const seen = new Set([anchor.value]);
        for (const suggestion of values) {
            if (!suggestion.anchorLocal || seen.has(suggestion.anchorLocal)) continue;
            seen.add(suggestion.anchorLocal);
            const button = document.createElement('button');
            button.type = 'button'; button.className = 'btn btn-sm btn-outline-secondary';
            button.textContent = format(getLocalizedText('oncall-start-at', 'Start at {0}'), localText(suggestion.anchorLocal));
            button.addEventListener('click', () => {
                anchor.value = suggestion.anchorLocal; markManual(); anchor.focus(); updatePreview();
            });
            suggestions.append(button);
        }
    }
    async function updatePreview(suggest = false) {
        if (suggest && !manual) needsSuggestion = true;
        const id = ++sequence;
        preview.replaceChildren(); warnings.replaceChildren(); warnings.hidden = true; suggestions.replaceChildren();
        document.getElementById('oncall-preview-zone').textContent = zone.value;
        if (manual && !anchor.value && !anchor.readOnly) {
            preview.setAttribute('aria-busy', 'false');
            status.textContent = getLocalizedText('oncall-enter-start', 'Enter a scheduling start point to preview shifts.');
            return;
        }
        status.textContent = getLocalizedText('oncall-preview-loading', 'Updating shift preview…');
        preview.setAttribute('aria-busy', 'true');
        const askForAnchor = anchor.readOnly || (!manual && needsSuggestion);
        const users = [...selected.children].map(row => ({ id: row.dataset.userId, name: row.dataset.name }));
        try {
            const response = await fetch(form.dataset.previewUrl, {
                method: 'POST', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: form.querySelector('[name="__RequestVerificationToken"]').value },
                body: JSON.stringify({ groupId: form.dataset.groupId || null, ruleId: template.value || null,
                    timeZoneId: zone.value, anchorLocal: askForAnchor ? null : anchor.value, userIds: users.map(user => user.id) })
            });
            const data = await response.json();
            if (id !== sequence) return;
            if (!response.ok) {
                status.textContent = data.errors?.length ? data.errors.join(' ') : getLocalizedText('oncall-preview-failed', 'Unable to update the preview. Please try again.');
                return;
            }
            if (askForAnchor && data.anchorLocal) {
                anchor.value = data.anchorLocal;
                needsSuggestion = false;
                notice.hidden = !template.value || anchor.readOnly || !!data.anchorLocked;
            }
            if (data.anchorLocked) anchor.readOnly = true;
            showSuggestions(data.suggestions || []);
            for (const warning of data.warnings || []) {
                const item = document.createElement('li'); item.textContent = warning; warnings.append(item);
            }
            warnings.hidden = !warnings.children.length;
            if (data.errors?.length) { status.textContent = data.errors.join(' '); return; }
            const names = new Map(users.map(user => [user.id, user.name]));
            for (const slot of (data.slots || []).slice(0, 4)) {
                const item = document.createElement('li'), people = document.createElement('strong'), times = document.createElement('div');
                item.className = 'list-group-item text-break';
                people.textContent = slot.userIds.length ? slot.userIds.map(userId => names.get(userId) || userId).join(', ')
                    : getLocalizedText('oncall-unassigned', 'Unassigned');
                times.className = 'small text-muted';
                times.textContent = `${localText(slot.startLocal)} – ${localText(slot.endLocal)}`;
                item.append(people, times); preview.append(item);
            }
            status.textContent = preview.children.length ? '' : template.value
                ? getLocalizedText('oncall-preview-add-members', 'Add on-call members to preview the rotation.')
                : getLocalizedText('oncall-preview-select-template', 'Choose a scheduling template to preview the rotation.');
        } catch {
            if (id !== sequence) return;
            preview.replaceChildren();
            status.textContent = getLocalizedText('oncall-preview-failed', 'Unable to update the preview. Please try again.');
        } finally {
            if (id === sequence) preview.setAttribute('aria-busy', 'false');
        }
    }
    anchor.addEventListener('input', () => { if (!anchor.readOnly) { markManual(); updatePreview(); } });
    anchor.addEventListener('change', () => { if (!anchor.readOnly) { markManual(); updatePreview(); } });
    template.addEventListener('change', () => { updateAutoExtend(); updatePreview(true); });
    form.addEventListener('oncall-members-changed', () => updatePreview());
    if (typeof $ === 'function') {
        $('#GroupInput_TimeZoneId').select2({
            width: '100%', minimumResultsForSearch: 0,
            language: { noResults: () => getLocalizedText('no-matching-timezone-found', 'No matching timezone found') }
        }).on('change', () => updatePreview(true));
    } else zone.addEventListener('change', () => updatePreview(true));
    updateAutoExtend();
    updatePreview();
})();
