(() => {
    'use strict';
    const input = document.getElementById('delete-team-name');
    if (!input) return;
    const validate = () => input.setCustomValidity(input.value === input.dataset.teamName ? '' : input.dataset.mismatch);
    input.addEventListener('input', validate);
    validate();
})();
