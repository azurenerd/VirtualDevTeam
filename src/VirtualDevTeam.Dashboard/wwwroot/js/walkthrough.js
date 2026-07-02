// Keyboard navigation for the Walkthrough page
window.walkthroughKeyHandler = {
    _dotNetRef: null,
    _handler: null,

    init: function (dotNetRef) {
        this.dispose();
        this._dotNetRef = dotNetRef;
        this._handler = function (e) {
            if (e.key === 'ArrowLeft') {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnArrowKey', 'left');
            } else if (e.key === 'ArrowRight') {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnArrowKey', 'right');
            }
        };
        document.addEventListener('keydown', this._handler);
    },

    dispose: function () {
        if (this._handler) {
            document.removeEventListener('keydown', this._handler);
            this._handler = null;
        }
        this._dotNetRef = null;
    }
};
