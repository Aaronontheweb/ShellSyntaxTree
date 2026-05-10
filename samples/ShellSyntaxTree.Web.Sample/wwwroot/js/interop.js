window.shellSyntaxTreeInterop = {
    renderMermaid: async function (containerId, source) {
        const el = document.getElementById(containerId);
        if (!el) return;
        try {
            const { svg } = await mermaid.render(containerId + '-svg', source);
            el.innerHTML = svg;
        } catch (e) {
            el.innerHTML = '<pre style="color:#b00">Mermaid render error: ' + (e.message || e) + '</pre>';
        }
    },
    copyText: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            return false;
        }
    }
};
