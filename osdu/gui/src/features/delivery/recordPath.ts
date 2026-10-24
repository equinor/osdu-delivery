// A place in an OSDU record as the record views write it (`data.GeoContexts[1].GeoPoliticalEntityID`), read as the property
// names and list places it steps through. The dimension builder and the duplicate key's builder both pick values by it.

/** Where a value is in a record: property names and array places, from the record's root. */
export type Location = (string | number)[];

/**
 * The location an inspector's path names: `data.GeoContexts[1].GeoPoliticalEntityID` is data, GeoContexts, item 1 of it,
 * then GeoPoliticalEntityID.
 */
export function locationOf(path: string): Location {
  const location: Location = [];
  for (const match of path.matchAll(/\[(\d+)\]|([^.[\]]+)/g)) {
    location.push(match[1] !== undefined ? Number(match[1]) : match[2]);
  }

  return location;
}

/** The property names of a location, its array places left out, as a path names them. */
export function namesOf(location: Location): string[] {
  return location.filter((part): part is string => typeof part === "string");
}
