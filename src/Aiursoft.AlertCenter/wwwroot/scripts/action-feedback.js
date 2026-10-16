(() => {
    'use strict';
    const messages = JSON.parse(document.getElementById('alertcenter-localization')?.textContent || '{}');


    const dialog = document.getElementById('action-feedback');
    if (!dialog || !window.bootstrap || !window.fetch) return;
    const title = document.getElementById('action-feedback-title');
    const message = document.getElementById('action-feedback-message');
    const refresh = document.getElementById('action-feedback-refresh');
    const returnButton = document.getElementById('action-feedback-return');
    let refreshConfirmation = false;
    let errorTitle;
    let errorMessage;
    let sourceModal;
    let sourceControl;
    let restoreSource = false;
    const openModals = new WeakSet();
    document.addEventListener('show.bs.modal', event => { openModals.add(event.target); });
    document.addEventListener('hidden.bs.modal', event => { openModals.delete(event.target); });

    function resetRefreshConfirmation() {
        refreshConfirmation = false;
        refresh.textContent = (messages["Refresh page"] ?? "Refresh page");
        refresh.classList.remove('btn-warning');
        refresh.classList.add('btn-outline-secondary');
        returnButton.textContent = (messages["Back to Edit"] ?? "Back to Edit");
        returnButton.setAttribute('data-bs-dismiss', 'modal');
    }

    refresh.addEventListener('click', () => {
        if (refreshConfirmation) {
            window.location.reload();
            return;
        }
        errorTitle = title.textContent;
        errorMessage = message.textContent;
        refreshConfirmation = true;
        title.textContent = (messages["Confirm page refresh?"] ?? "Confirm page refresh?");
        message.textContent = (messages["Refreshing will discard unsaved input. Select \"Cancel refresh\" to return to error details and retain current input."] ?? "Refreshing will discard unsaved input. Select \"Cancel refresh\" to return to error details and retain current input.");
        refresh.textContent = (messages["Discard input and refresh"] ?? "Discard input and refresh");
        refresh.classList.remove('btn-outline-secondary');
        refresh.classList.add('btn-warning');
        returnButton.textContent = (messages["Cancel refresh"] ?? "Cancel refresh");
        returnButton.removeAttribute('data-bs-dismiss');
        returnButton.focus();
    });
    returnButton.addEventListener('click', event => {
        if (!refreshConfirmation) return;
        event.stopPropagation();
        resetRefreshConfirmation();
        title.textContent = errorTitle;
        message.textContent = errorMessage;
        refresh.focus();
    });
    dialog.addEventListener('hidden.bs.modal', () => {
        resetRefreshConfirmation();
        if (restoreSource && sourceModal?.isConnected) {
            sourceModal.addEventListener('shown.bs.modal', () => sourceControl?.focus(), { once: true });
            bootstrap.Modal.getOrCreateInstance(sourceModal).show();
        } else if (sourceControl?.isConnected) {
            sourceControl.focus();
        }
    });

    function showError(text, stale, form, submitter) {
        resetRefreshConfirmation();
        const modalParent = form.closest('.modal');
        sourceModal = modalParent && (openModals.has(modalParent) || modalParent.classList.contains('show')) ? modalParent : null;
        sourceControl = submitter || form.querySelector('input:not([type="hidden"]), textarea, select, button');
        restoreSource = Boolean(sourceModal);
        title.textContent = stale ? (messages["Page information updated"] ?? "Page information updated") : (messages["Action not completed"] ?? "Action not completed");
        message.textContent = text;
        refresh.classList.toggle('d-none', !stale);
        const show = () => bootstrap.Modal.getOrCreateInstance(dialog).show();
        if (sourceModal) {
            const source = sourceModal;
            const modal = bootstrap.Modal.getOrCreateInstance(source);
            const hide = () => modal.hide();
            source.addEventListener('shown.bs.modal', hide, { once: true });
            source.addEventListener('hidden.bs.modal', () => {
                source.removeEventListener('shown.bs.modal', hide);
                show();
            }, { once: true });
            hide();
        } else {
            show();
        }
    }

    function navigateTo(url) {
        const destination = new URL(url, window.location.href);
        if (destination.origin !== window.location.origin) throw new Error('Unexpected redirect');
        if (destination.pathname === window.location.pathname && destination.search === window.location.search) {
            window.location.hash = destination.hash;
            window.location.reload();
        } else {
            window.location.assign(destination.href);
        }
    }

    document.addEventListener('submit', async event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-action-feedback') ||
            form.method.toLowerCase() !== 'post') return;
        if (event.defaultPrevented || !form.reportValidity()) return;
        const action = new URL(event.submitter?.hasAttribute('formaction') ? event.submitter.formAction : form.action, window.location.href);
        if (action.origin !== window.location.origin) return;
        const submitter = event.submitter;
        const body = new FormData(form);
        if (submitter?.name) body.append(submitter.name, submitter.value);
        event.preventDefault();
        if (form.dataset.submitting === 'true') return;
        form.dataset.submitting = 'true';
        const buttons = [...form.querySelectorAll('button[type="submit"], button:not([type]), input[type="submit"]')];
        const previousDisabled = buttons.map(button => button.disabled);
        buttons.forEach(button => { button.disabled = true; });
        form.setAttribute('aria-busy', 'true');
        try {
            const response = await fetch(action.href, {
                method: 'POST', body, credentials: 'same-origin',
                headers: { 'X-AlertCenter-Dialog': 'true', 'Accept': 'application/json' }
            });
            if (response.redirected) {
                navigateTo(response.url);
                return;
            }
            const json = response.headers.get('Content-Type')?.includes('application/json') ? await response.json() : null;
            if (response.ok && typeof json?.redirectUrl === 'string') {
                navigateTo(json.redirectUrl);
                return;
            }
            const fallback = response.status === 403 ? (messages["You do not have permission to perform this action."] ?? "You do not have permission to perform this action.") :
                response.status === 404 ? (messages["The target object no longer exists. Please refresh the page."] ?? "The target object no longer exists. Please refresh the page.") :
                response.status === 400 ? (messages["Request invalid or page credentials expired. Check input and refresh to retry if necessary."] ?? "Request invalid or page credentials expired. Check input and refresh to retry if necessary.") :
                (messages["The result of the action cannot be confirmed at this time. Please verify the page status before resubmitting."] ?? "The result of the action cannot be confirmed at this time. Please verify the page status before resubmitting.");
            showError(typeof json?.message === 'string' ? json.message : fallback,
                response.status === 409 || response.status === 404 || json?.refresh === true, form, submitter);
        } catch {
            showError((messages["Network error; the operation result cannot be confirmed yet. Please verify the status before resubmitting to avoid duplicates."] ?? "Network error; the operation result cannot be confirmed yet. Please verify the status before resubmitting to avoid duplicates."), false, form, submitter);
        } finally {
            buttons.forEach((button, index) => { button.disabled = previousDisabled[index]; });
            delete form.dataset.submitting;
            form.removeAttribute('aria-busy');
        }
    });
})();
