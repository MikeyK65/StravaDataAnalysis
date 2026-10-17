// Small JS helpers used by the shared UI (works in both Blazor Server and BlazorWebView).
export function prefersDark() {
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
}

// Starts a browser download from a same-origin URL without leaving the page.
export function downloadUrl(url) {
    const a = document.createElement('a');
    a.href = url;
    a.download = '';
    document.body.appendChild(a);
    a.click();
    a.remove();
}
