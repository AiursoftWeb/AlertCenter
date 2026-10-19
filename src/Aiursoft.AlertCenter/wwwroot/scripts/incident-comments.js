(() => {
    'use strict';

    const root = document.getElementById('incident-comments');
    if (!root) return;
    window.require?.config({ paths: { vs: root.dataset.monacoPath } });

    const deleteModal = document.getElementById('delete-incident-comment');
    deleteModal?.addEventListener('show.bs.modal', event => {
        if (!event.relatedTarget) return;
        const trigger = event.relatedTarget;
        const id = trigger.dataset.commentId || '';
        document.getElementById('delete-incident-comment-id').value = id;
        document.getElementById('delete-incident-comment-author').textContent = trigger.dataset.commentAuthor || '';
        document.getElementById('delete-incident-comment-created').textContent = trigger.closest('.ac-timeline-item')?.querySelector('[data-utc-time]')?.textContent || '';
        document.getElementById('delete-incident-comment-confirm').disabled = !id;
    });

    const markdownUi = window.AiursoftMarkdownUi;
    if (markdownUi) {
        const bodies = [];
        root.querySelectorAll('.comment-markdown-source').forEach(source => {
            const body = source.nextElementSibling;
            if (!body?.classList.contains('comment-markdown-body')) return;
            try {
                body.innerHTML = markdownUi.renderMarkdown(source.value);
                body.classList.remove('comment-markdown-fallback');
                bodies.push(body);
            } catch (error) {
                console.error('Failed to render incident comment Markdown.', error);
            }
        });
        void markdownUi.initializeMarkdownReader({ container: bodies, mermaidTheme: mermaidTheme() }).catch(error => {
            console.error('Failed to initialize incident comment readers.', error);
        });
    }

    const form = document.getElementById('incident-comment-form');
    if (!form) return;
    const source = document.getElementById('incident-comment-source');
    const editorHost = document.getElementById('incident-comment-editor');
    const editorPane = document.getElementById('incident-comment-editor-pane');
    const previewPane = document.getElementById('incident-comment-preview-pane');
    const errorMessage = document.getElementById('incident-comment-error');
    const count = document.getElementById('incident-comment-count');
    const viewModes = document.getElementById('incident-comment-view-modes');
    let controller;

    function validationMessage(markdown) {
        if (!markdown.trim()) return getLocalizedText("enter-comment-content-cannot-contain-only-whitespace", "Enter comment content; cannot contain only whitespace.");
        if (markdown.length > source.maxLength) return getLocalizedText("comment-must-not-exceed-4-000-utf-16", "Comment must not exceed 4,000 UTF-16 characters. Please shorten the content before publishing.");
        return '';
    }

    function showValidation(message) {
        errorMessage.textContent = message;
        errorMessage.hidden = !message;
        source.setAttribute('aria-invalid', String(Boolean(message)));
        editorHost.setAttribute('aria-invalid', String(Boolean(message)));
    }

    function updateSource(markdown) {
        source.value = markdown;
        count.textContent = `${markdown.length} / ${source.maxLength}`;
        if (!errorMessage.hidden) showValidation(validationMessage(markdown));
    }

    function focusEditor() {
        if (controller) {
            void controller.setViewMode('editor').then(() => controller.focus());
        } else {
            source.focus();
        }
    }

    source.addEventListener('input', () => updateSource(source.value));
    source.addEventListener('invalid', event => {
        event.preventDefault();
        showValidation(validationMessage(source.value));
        focusEditor();
    });
    form.addEventListener('submit', event => {
        controller?.syncTextarea();
        const message = validationMessage(source.value);
        showValidation(message);
        if (message) {
            event.preventDefault();
            focusEditor();
        }
    }, { capture: true });

    if (!markdownUi) return;

    function mermaidTheme() {
        return document.documentElement.dataset.bsTheme === 'dark' ? 'dark' : 'default';
    }

    function monacoTheme() {
        return document.documentElement.dataset.bsTheme === 'dark' ? 'vs-dark' : 'vs';
    }

    async function initializeEditor() {
        editorHost.hidden = false;
        try {
            controller = await markdownUi.createMarkdownEditor({
                editorContainer: editorHost,
                textarea: source,
                previewContainer: document.getElementById('incident-comment-preview'),
                editorPane,
                previewPane,
                form,
                loadMonaco: () => markdownUi.loadMonacoFromAmd(),
                theme: monacoTheme(),
                mermaidTheme: mermaidTheme(),
                uploadUrl: form.dataset.imageUploadUrl,
                imageUploadOptions: {
                    onError: error => {
                        console.error('Failed to upload an incident comment image.', error);
                        const uploadError = document.getElementById('incident-comment-upload-error');
                        uploadError.textContent = getLocalizedText("failed-upload-image", "Failed to upload image. Please try again.");
                        uploadError.hidden = false;
                    }
                },
                initialViewMode: window.matchMedia('(max-width: 767.98px)').matches ? 'editor' : 'split',
                viewModeControls: [...viewModes.querySelectorAll('[data-comment-view-mode]')].map(element => ({
                    element,
                    mode: element.dataset.commentViewMode
                })),
                editorOptions: { ariaLabel: getLocalizedText("comment-content-markdown", "Comment content (Markdown)") },
                onChange: updateSource,
                onSave: () => form.requestSubmit(),
                onInitializationError: error => {
                    source.required = true;
                    console.warn('Monaco is unavailable; using the incident comment textarea.', error);
                },
                onPreviewError: error => {
                    console.error('Failed to render the incident comment preview.', error);
                }
            });
            source.required = controller.isFallback;
            viewModes.hidden = false;
            if (!controller.isFallback) {
                const observer = new MutationObserver(() => window.monaco.editor.setTheme(monacoTheme()));
                observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme'] });
            }
        } catch (error) {
            source.style.removeProperty('display');
            source.required = true;
            editorHost.hidden = true;
            editorPane.hidden = false;
            previewPane.hidden = true;
            viewModes.hidden = true;
            console.error('Failed to initialize the incident comment editor; using the textarea.', error);
        }
    }

    void initializeEditor();
})();
