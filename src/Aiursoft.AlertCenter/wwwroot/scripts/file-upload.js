import Uploader from './uploader.js';

window.addEventListener('load', () => {
    document.querySelectorAll('[data-file-upload]').forEach(component => {
        function getUploadText(key, defaultText) {
            const el = component.querySelector(`[data-upload-localization] span[data-key="${key}"]`);
            return el ? el.textContent : defaultText;
        }
        new Uploader({
            fileInput: $(component.querySelector('input[type="file"]')),
            progress: $(component.querySelector('.progress')),
            progressbar: $(component.querySelector('.progress-bar')),
            addressInput: $(component.querySelector('input[name]')),
            sizeInMb: Number(component.dataset.sizeInMb),
            validExtensions: (component.dataset.validExtensions || '').split(' ').filter(Boolean),
            uploadUrl: component.dataset.uploadUrl
        }).init({
            messages: {
                'default': getUploadText("default", "Drag and drop a file here or click"),
                'replace': getUploadText("replace", "Drag and drop or click to replace"),
                'remove': getUploadText("remove", "Remove"),
                'error': getUploadText("error", "Ooops, something wrong happended."),
            },
            error: {
                'fileSize': getUploadText("fileSize", "The file size is too big ({{ value }} max)."),
                'minWidth': getUploadText("minWidth", "The image width is too small ({{ value }}px min)."),
                'maxWidth': getUploadText("maxWidth", "The image width is too big ({{ value }}px max)."),
                'minHeight': getUploadText("minHeight", "The image height is too small ({{ value }}px min)."),
                'maxHeight': getUploadText("maxHeight", "The image height is too big ({{ value }}px max)."),
                'imageFormat': getUploadText("imageFormat", "The image format is not allowed ({{ value }} only)."),
                'fileExtension': getUploadText("fileExtension", "The file is not allowed ({{ value }} only)."),
            }
        });
    });
});
