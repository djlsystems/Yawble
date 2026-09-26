/**
 * The only user-facing product title.
 * Renaming the product changes this file (and the window title in package.json) and nothing under src that
 * is not presentation. Do not put a brand name in environment variables, paths, or identifiers.
 */
export const productTitle = 'Yawble'

export const productTagline = 'Run teams of coding agents on your own machine.'

/** The full logo with dark lettering, for the light theme. 1066 x 365, transparent. */
export const productLogoLight = '/logo-light.png'

/** The full logo with white lettering, for the dark theme. 1066 x 365, transparent. */
export const productLogoDark = '/logo-dark.png'

/** The logo for the theme that is showing. */
export const productLogoFor = (dark: boolean) => (dark ? productLogoDark : productLogoLight)
