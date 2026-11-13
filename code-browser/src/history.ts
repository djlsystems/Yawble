/** Where the dialog is: a folder, and the file previewed in it (or none). */
export interface Place { dir: string; file: string | null }

/** Back and forward inside the dialog: a list of places and a cursor, like a browser's. */
export class PlaceHistory {
  private places: Place[] = [];
  private at = -1;

  get current(): Place | null { return this.places[this.at] ?? null; }
  get canBack(): boolean { return this.at > 0; }
  get canForward(): boolean { return this.at < this.places.length - 1; }

  /** A new place: drops what was ahead of the cursor; the same place twice in a row is kept once. */
  push(place: Place): void {
    const cur = this.current;
    if (cur && cur.dir === place.dir && cur.file === place.file) return;
    this.places = this.places.slice(0, this.at + 1);
    this.places.push(place);
    this.at = this.places.length - 1;
  }

  back(): Place | null {
    if (!this.canBack) return null;
    this.at--;
    return this.current;
  }

  forward(): Place | null {
    if (!this.canForward) return null;
    this.at++;
    return this.current;
  }
}
