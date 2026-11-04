/**
 * THE TEAM PICTURE: an optional site per team, made from the platform's template and kept current
 * from the platform's own figures (docs/sites.md, "The Team picture"). One wording for every place
 * that offers it - New Team, Team settings, the solution install wizard's team step - so a person
 * reads the same choice wherever they meet it.
 */

/** The site name the platform reserves for it, so it never clashes with a team's or a package's. */
export const TeamPictureSite = 'team-picture';

export const TeamPictureLabel = 'Team picture';

export const TeamPictureHint =
  "A page showing what this team builds or runs and how far along it is, kept current from the platform's own " +
  'figures. It opens from the globe beside the team\'s name. Turning it off takes the page down and keeps its data.';

/** The globe link's words for a team's site: the Team picture by its own name, any other by its slug. */
export function siteLinkWords(name: string): string {
  return name === TeamPictureSite ? 'the Team picture' : `the ${name} site`;
}
