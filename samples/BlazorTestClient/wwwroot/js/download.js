window.downloadFile = async function (fileName, fileContent) {
    const blob = new Blob([fileContent]);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
}