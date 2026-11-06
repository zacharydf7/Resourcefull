// The navbar logo's navy R would vanish on the dark theme's background, so it has a light version.
function matchLogoToTheme() {
  const logo = document.getElementById('logo')
  if (!logo) return
  const dark = document.documentElement.getAttribute('data-bs-theme') === 'dark'
  logo.src = logo.src.replace(/logo(-dark)?\.svg$/, dark ? 'logo-dark.svg' : 'logo.svg')
}

// Types each package's name in turn on the landing page's install line, as a terminal would: it erases back to the
// prefix it shares with the next name, then types the rest. Visitors who ask for reduced motion, and those without
// scripts, see only the first name, with no animation. A button at the end of the line pauses the animation. Copying
// the line gives the whole command for the name shown, on one line, even partway through typing; without that, the
// browser would put each item of the line's flex box on a line of its own. Copying just the name gives the whole name
// too.
function typePackageNames() {
  const name = document.querySelector('.rf-install-name')
  if (!name) return
  const packages = name.dataset.packages.split(' ')
  const line = name.closest('.rf-install')
  let index = 0
  line.addEventListener('copy', event => {
    // A selection that runs past the line is copied as the browser would copy it.
    const selection = document.getSelection()
    if (!line.contains(selection.anchorNode) || !line.contains(selection.focusNode)) return
    const nameOnly = name.contains(selection.anchorNode) && name.contains(selection.focusNode)
    event.clipboardData.setData('text/plain', nameOnly ? packages[index] : `dotnet add package ${packages[index]}`)
    event.preventDefault()
  })
  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return

  const holdMs = 2200
  const typeMs = 90
  const eraseMs = 40
  // Screen readers get one stable command rather than every keystroke.
  name.setAttribute('aria-hidden', 'true')
  name.after(Object.assign(document.createElement('span'), { className: 'visually-hidden', textContent: packages[0] }))
  // The longest name's width, and the cursor's, so the line doesn't change width as it types.
  name.style.minWidth = `${Math.max(...packages.map(p => p.length)) + 1}ch`
  name.classList.add('rf-typing')

  let shown = packages[0].length
  let erasing = false
  let timer
  // WCAG 2.2.2: motion that starts on its own and lasts more than five seconds needs a way to pause it on the page, not
  // only a system setting.
  const pause = Object.assign(document.createElement('button'), { type: 'button', className: 'rf-install-pause' })
  pause.setAttribute('aria-label', 'Pause the animation')
  pause.setAttribute('aria-pressed', 'false')
  pause.innerHTML = '<i class="bi bi-pause-fill"></i>'
  pause.addEventListener('click', () => {
    const paused = pause.getAttribute('aria-pressed') !== 'true'
    pause.setAttribute('aria-pressed', String(paused))
    pause.innerHTML = `<i class="bi bi-${paused ? 'play' : 'pause'}-fill"></i>`
    clearTimeout(timer)
    name.classList.toggle('rf-typing', !paused)
    if (paused) {
      // Stopped on a whole name, so what's shown is a command that works.
      erasing = false
      shown = packages[index].length
      show()
    } else {
      timer = setTimeout(step, holdMs)
    }
  })
  line.append(pause)
  const shared = (a, b) => {
    let length = 0
    while (length < a.length && a[length] === b[length]) length++
    return length
  }
  const show = () => { name.textContent = packages[index].slice(0, shown) }
  const step = () => {
    if (erasing) {
      const next = (index + 1) % packages.length
      if (shown > shared(packages[index], packages[next])) {
        shown--
        show()
        timer = setTimeout(step, eraseMs)
        return
      }
      erasing = false
      index = next
    }
    if (shown < packages[index].length) {
      shown++
      show()
      // A name typed in full stays a while before it's erased.
      timer = setTimeout(step, shown === packages[index].length ? holdMs : typeMs)
      return
    }
    erasing = true
    timer = setTimeout(step, eraseMs)
  }
  timer = setTimeout(step, holdMs)
}

export default {
  start: () => {
    matchLogoToTheme()
    typePackageNames()
    new MutationObserver(matchLogoToTheme).observe(document.documentElement, { attributeFilter: ['data-bs-theme'] })
  },
  iconLinks: [
    { icon: 'box-seam', href: 'https://www.nuget.org/packages/Resourcefull', title: 'NuGet' }
  ]
}
