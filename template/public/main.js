// The navbar logo's navy R would vanish on the dark theme's background, so it has a light version.
function matchLogoToTheme() {
  const logo = document.getElementById('logo')
  if (!logo) return
  const dark = document.documentElement.getAttribute('data-bs-theme') === 'dark'
  logo.src = logo.src.replace(/logo(-dark)?\.svg$/, dark ? 'logo-dark.svg' : 'logo.svg')
}

export default {
  start: () => {
    matchLogoToTheme()
    new MutationObserver(matchLogoToTheme).observe(document.documentElement, { attributeFilter: ['data-bs-theme'] })
  },
  iconLinks: [
    { icon: 'github', href: 'https://github.com/zacharydf7/Resourcefull', title: 'GitHub' },
    { icon: 'box-seam', href: 'https://www.nuget.org/packages/Resourcefull', title: 'NuGet' }
  ]
}
