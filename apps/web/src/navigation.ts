/**
 * The sections of the application, in the order they are shown.
 *
 * One list, read twice — by the rail in `AppShell` and by the command bar's
 * "Go to" commands — because two hand-written copies of a navigation list is
 * how a new screen ends up reachable from one of them and not the other.
 *
 * `to` is a union of literals rather than `string` on purpose: that is what
 * lets `Link` and `navigate` keep checking the paths against the route tree.
 * A new section is a compile error here until the route exists.
 */
export type NavItem = {
  readonly to:
    | '/'
    | '/settings/status'
    | '/library'
    | '/library/albums'
    | '/library/tracks'
    | '/library/composers'
    | '/library/compositions'
    | '/library/matching'
    | '/files'
    | '/acquire'
  readonly label: string
  /**
   * Whether the link is current only on exactly this path.
   *
   * True where a longer path belongs to a *different* section: every path
   * starts with "/", and `/library/albums` starts with `/library`, so without
   * this the dashboard link would be current everywhere and the artists link
   * would light up on the albums page.
   */
  readonly exact: boolean
  /**
   * Searched by the command bar, not shown anywhere. Required rather than
   * optional so a new section has to answer "what would someone type to find
   * this?" — the labels alone are four words, and "releases" is not one of them.
   */
  readonly keywords: readonly string[]
}

/**
 * The rail's headings. Browsing reads the library, its folders included;
 * management changes it — answers questions, spends money.
 */
export type NavSection = {
  readonly label: string
  readonly items: readonly NavItem[]
}

export const NAV_SECTIONS: readonly NavSection[] = [
  {
    label: 'General',
    items: [
      { to: '/', label: 'Home', exact: true, keywords: ['home', 'overview', 'stats', 'counts'] },
      {
        to: '/settings/status',
        label: 'Settings',
        exact: false,
        keywords: ['status', 'foundation', 'dashboard', 'scan', 'passes', 'health'],
      },
    ],
  },
  {
    label: 'Library',
    items: [
      {
        to: '/library',
        label: 'Artists',
        exact: true,
        keywords: ['library', 'people', 'performers'],
      },
      { to: '/library/albums', label: 'Albums', exact: false, keywords: ['releases', 'catalogue'] },
      {
        to: '/library/tracks',
        label: 'Tracks',
        exact: false,
        keywords: ['songs', 'recordings', 'play'],
      },
      {
        to: '/library/composers',
        label: 'Composers',
        exact: false,
        keywords: ['writers', 'songwriters', 'lyricists'],
      },
      {
        to: '/library/compositions',
        label: 'Compositions',
        exact: false,
        keywords: ['works', 'pieces', 'standards', 'covers'],
      },
      // The disk, rather than the catalogue: where somebody goes when a screen
      // above is wrong about something — a duplicate folder, a stray rip.
      {
        to: '/files',
        label: 'Files',
        exact: false,
        keywords: ['files', 'folders', 'disk', 'upload', 'delete', 'trash', 'rename'],
      },
    ],
  },
  {
    label: 'Management',
    items: [
      // Not exact, though nothing sits below it today — a question opens in a
      // dialog rather than at a path of its own. Left as it is because the rail
      // behaves identically either way here, and the next screen under /identify
      // would otherwise unhighlight the section it belongs to.
      {
        to: '/library/matching',
        label: 'Identify',
        exact: false,
        keywords: ['matching', 'questions', 'unmatched'],
      },
      // Last, and separate from Identify in more than order: that one corrects
      // what is already here, this one spends money on something that is not.
      {
        to: '/acquire',
        label: 'Acquire',
        exact: true,
        keywords: ['qobuz', 'download', 'buy', 'purchase', 'acquire'],
      },
    ],
  },
]

export const NAV_ITEMS: readonly NavItem[] = NAV_SECTIONS.flatMap((section) => section.items)
