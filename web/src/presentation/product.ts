/**
 * The only user-facing product title.
 * Renaming the product changes this file (and the window title in package.json) and nothing under src that
 * is not presentation. Do not put a brand name in environment variables, paths, or identifiers.
 */
export const productTitle = 'Yawble'

export const productTagline = 'A fleet of agents, moving with you.'

/** The line under the product's name where the logo animation ends. */
export const productSignOff = 'Focus on the work, not the agent.'

/** The product's web address as a person reads it, under that line. */
export const productAddress = 'yawble.ai'

/** The full logo with dark lettering, for the light theme. 1066 x 365, transparent. */
export const productLogoLight = '/logo-light.png'

/** The full logo with white lettering, for the dark theme. 1066 x 365, transparent. */
export const productLogoDark = '/logo-dark.png'

/** The logo for the theme that is showing. */
export const productLogoFor = (dark: boolean) => (dark ? productLogoDark : productLogoLight)

/** The head mark alone, the logo without its lettering, for a header row short of room. 128 x 128, transparent, one for both themes. */
export const productMark = '/icons/favicon-128x128.png'

/** The same head mark at 400 x 400, for where it is drawn large. Transparent, one for both themes. */
export const productMarkLarge = '/icons/mark-400x400.png'

/** The operator CLI's command, for sentences that tell a person what to run. */
export const productCli = 'yawble'
