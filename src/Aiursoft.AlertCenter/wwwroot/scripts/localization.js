function getLocalizedText(key, defaultText) {
    const el = document.querySelector(`#loc-data span[data-key="${key}"]`);
    return el ? el.textContent : defaultText;
}
