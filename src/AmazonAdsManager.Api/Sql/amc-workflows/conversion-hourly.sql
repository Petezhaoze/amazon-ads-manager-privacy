-- AMC attributed conversions bucketed by AD-SERVING hour (traffic-time attribution).
-- Empirical verification on amcjk0ydh5o, 2026-05-24:
--   At hourly grain GROUPed by (date, hour, campaign, tracked_asin, conversion_event_type),
--   AMC's privacy aggregation suppressed the date column to "" on 100% of returned rows.
--   Reducing the GROUP BY to (date, hour, campaign_id, campaign_name, ad_product_type) gives
--   each cell enough users to survive aggregation.
--
-- IMPORTANT SEMANTIC: this query uses `amazon_attributed_events_by_traffic_time`, NOT
-- `amazon_attributed_events_by_conversion_time`. For dayparting recommendations on bid
-- scheduling, we need to know which HOUR THE AD WAS SERVED that drove each conversion -- not
-- the hour the purchase happened. AMC docs: "Sponsored Products and Sponsored Display use
-- amazon_attributed_events_by_traffic_time and Sponsored Brands uses
-- amazon_attributed_events_by_conversion_time." This also makes the HourlyScorecard join
-- coherent: traffic-hourly and this query both share the same `traffic_event_hour` time axis.
--
-- We KEEP the output column aliases as `conversion_date` and `conversion_hour` so the existing
-- ingestion + DB schema (dbo.AmcConversionsHourly.ConversionDate/Hour) stay compatible without
-- a migration. Semantically those columns now hold the ad-serving date/hour of each attributed
-- conversion, not the purchase date/hour.
-- Trade-off: tracked_asin / conversion_event_type granularity is dropped. Same reasoning as
-- traffic-hourly.sql -- the HourlyScorecard only needs (Date, Hour).
-- Other notes:
--   - No trailing semicolon.
--   - traffic_event_date and traffic_event_hour are both advertiser-timezone columns.
--     timeWindowTimeZone controls request boundaries only; it does not shift these output fields.
--   - Use the dedicated traffic_event_date column instead of CAST(traffic_event_dt AS DATE);
--     the timestamp cast can inherit stricter aggregation thresholds and blank the date output.
--   - total_product_sales and new_to_brand_total_product_sales are in local currency (NO /1e8).

SELECT
  traffic_event_date AS conversion_date,
  traffic_event_hour AS conversion_hour,
  campaign_id_string AS campaign_id,
  campaign AS campaign_name,
  ad_product_type,
  SUM(purchases) AS purchases,
  SUM(units_sold) AS units_sold,
  SUM(total_product_sales) AS sales,
  SUM(new_to_brand_purchases) AS new_to_brand_purchases,
  SUM(new_to_brand_total_product_sales) AS new_to_brand_sales
FROM amazon_attributed_events_by_traffic_time
GROUP BY
  traffic_event_date,
  traffic_event_hour,
  campaign_id_string,
  campaign,
  ad_product_type
