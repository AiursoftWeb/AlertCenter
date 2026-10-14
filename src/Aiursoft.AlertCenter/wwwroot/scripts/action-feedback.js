(() => {
    'use strict';

    const dialog = document.getElementById('action-feedback');
    if (!dialog || !window.bootstrap || !window.fetch) return;
    const title = document.getElementById('action-feedback-title');
    const message = document.getElementById('action-feedback-message');
    const refresh = document.getElementById('action-feedback-refresh');
    let sourceModal;
    let sourceControl;
    let restoreSource = false;
    const openModals = new WeakSet();
    document.addEventListener('show.bs.modal', event => { openModals.add(event.target); });
    document.addEventListener('hidden.bs.modal', event => { openModals.delete(event.target); });

    refresh.addEventListener('click', () => {
        if (window.confirm('刷新将丢弃尚未保存的输入，确定继续？')) window.location.reload();
    });
    dialog.addEventListener('hidden.bs.modal', () => {
        if (restoreSource && sourceModal?.isConnected) {
            sourceModal.addEventListener('shown.bs.modal', () => sourceControl?.focus(), { once: true });
            bootstrap.Modal.getOrCreateInstance(sourceModal).show();
        } else if (sourceControl?.isConnected) {
            sourceControl.focus();
        }
    });

    function showError(text, stale, form, submitter) {
        const modalParent = form.closest('.modal');
        sourceModal = modalParent && (openModals.has(modalParent) || modalParent.classList.contains('show')) ? modalParent : null;
        sourceControl = submitter || form.querySelector('input:not([type="hidden"]), textarea, select, button');
        restoreSource = Boolean(sourceModal);
        title.textContent = stale ? '页面信息已更新' : '操作未完成';
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
                const destination = new URL(response.url, window.location.href);
                if (destination.origin === window.location.origin) {
                    window.location.assign(destination.href);
                    return;
                }
                throw new Error('Unexpected redirect');
            }
            const json = response.headers.get('Content-Type')?.includes('application/json') ? await response.json() : null;
            if (response.ok && typeof json?.redirectUrl === 'string') {
                const destination = new URL(json.redirectUrl, window.location.href);
                if (destination.origin !== window.location.origin) throw new Error('Unexpected redirect');
                window.location.assign(destination.href);
                return;
            }
            const fallback = response.status === 403 ? '没有执行此操作的权限。' :
                response.status === 404 ? '操作对象已不存在，请刷新页面。' :
                response.status === 400 ? '请求无效或页面凭据已过期，请检查输入，必要时刷新后重试。' :
                '操作结果暂时无法确认。请先核实页面状态，不要重复提交。';
            showError(typeof json?.message === 'string' ? json.message : fallback,
                response.status === 409 || response.status === 404 || json?.refresh === true, form, submitter);
        } catch {
            showError('网络异常，操作结果暂时无法确认。请先核实状态，不要重复提交。', false, form, submitter);
        } finally {
            buttons.forEach((button, index) => { button.disabled = previousDisabled[index]; });
            delete form.dataset.submitting;
            form.removeAttribute('aria-busy');
        }
    });
})();
