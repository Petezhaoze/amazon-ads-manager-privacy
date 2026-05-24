-- AMC conversion-time metrics by conversion hour.
-- Verified by AMC Agent on amcjk0ydh5o for date range Apr 25 - May 24, 2026.
-- Notes:
--   - Date range is set by the workflow execution's timeWindowStart/End (no WHERE filter needed).
--   - conversion_event_hour is a direct integer column (advertiser timezone); use it instead of EXTRACT.
--   - total_product_sales and new_to_brand_total_product_sales are in local currency, NOT micro-microcents
--     (only `spend` and `supply_cost` need /1e8).
--   - time_zone literal removed: event timestamps here are advertiser TZ, labeling them 'UTC' was misleading.

SELECT
  CAST(conversion_event_dt AS DATE) AS conversion_date,
  conversion_event_hour AS conversion_hour,
  campaign_id_string AS campaign_id,
  campaign AS campaign_name,
  ad_product_type,
  tracked_asin,
  conversion_event_type,
  SUM(purchases) AS purchases,
  SUM(units_sold) AS units_sold,
  SUM(total_product_sales) AS sales,
  SUM(new_to_brand_purchases) AS new_to_brand_purchases,
  SUM(new_to_brand_total_product_sales) AS new_to_brand_sales
FROM amazon_attributed_events_by_conversion_time
GROUP BY
  CAST(conversion_event_dt AS DATE),
  conversion_event_hour,
  campaign_id_string,
  campaign,
  ad_product_type,
  tracked_asin,
  conversion_event_type;
