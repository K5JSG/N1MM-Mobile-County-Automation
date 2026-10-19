# Third-party notices

N1MM Mobile County Automation itself is released under the GNU General Public License v3.0 (`License.txt`).
The embedded county data in `src/N1MMMobileCounty.Core/Data/` is derived from the sources below.

## County boundaries

| Data | Source | License |
| --- | --- | --- |
| US counties (cb_2024_us_county_500k) | [US Census Bureau cartographic boundary files](https://www.census.gov/geographies/mapping-files/time-series/geo/cartographic-boundary.html) | Public domain (US government work) |
| US ZIP code tabulation areas (cb_2020_us_zcta520_500k), used for the Hawaii districts | [US Census Bureau cartographic boundary files](https://www.census.gov/geographies/mapping-files/time-series/geo/cartographic-boundary.html) | Public domain (US government work) |
| Hawaii QSO Party district-to-ZIP lists | Multiplier maps at hawaiiqsoparty.org/mult-map/ | Facts of the contest rules, read off the sponsor's maps |
| Canadian federal electoral districts, 2023 representation order | [Elections Canada](https://www.elections.ca/) electoral district boundary files | [Open Government Licence - Canada](https://open.canada.ca/en/open-government-licence-canada) |
| Canadian census divisions, census subdivisions and economic regions (2021) | [Statistics Canada cartographic boundary files](https://www12.statcan.gc.ca/census-recensement/2021/geo/sip-pis/boundary-limites/index2021-eng.cfm?year=21) | [Open Government Licence - Canada](https://open.canada.ca/en/open-government-licence-canada) |

Boundaries are generalised (1:500 000 or coarser, and thinned further for some Canadian files); accuracy is a few hundred metres.

## County codes and names

- The county abbreviations for each QSO party are published by that party's sponsor in its rules and multiplier lists (they are facts about the contest, not anyone's creative work). For convenience the complete lists were read from [N1MM Logger+](https://n1mmwp.hamdocs.com/) (`SupportFiles\QSOParty.sec`), because those are exactly the codes N1MM Logger+ accepts. N1MM Logger+ is by Tom Wagner, N1MM, and is not affiliated with this project.
- New York: the 2026 New York QSO Party rules (v1.1).
- Canadian Prairies QSO Party: district codes from [cpqp.ve6hams.ca](https://cpqp.ve6hams.ca/multipliers/).
- Ontario QSO Party: the OQP multiplier list (effective 2023-01-01), [va3cco.com/oqp](http://www.va3cco.com/oqp/OQPMultList.pdf).
- Atlantic Canada QSO Party: [acqp.ca resources](https://acqp.ca/resources/).
- The mapping of each remaining code to its county was worked out by `tools/BuildPartyData` (see the README).
