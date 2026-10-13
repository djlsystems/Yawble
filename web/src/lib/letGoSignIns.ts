/**
 * Sign-ins with a code a person let go of with Back in Add connection, by flow id. The Host keeps a
 * waiting flow until its code expires and lists it as the person's own, and Add connection picks such
 * a flow back up when it opens; one let go of here is not picked up again on this page, so the dialog
 * starts at the service. The Host's flow is left to expire: a code already typed still completes it.
 */
export const letGoSignIns = new Set<string>();
