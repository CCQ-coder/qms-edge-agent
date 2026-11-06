-- S1 product 34 (Jin Dali): properties from the two active EAP routes
-- /Equip/UploadJdl and /IO/UploadProcessQualityParameters.
-- Apply while product remains published. Then delete only Redis key TSL:34;
-- S1 rebuilds the model cache on its next model read. Re-running adds no rows.
SET NAMES utf8mb4;
START TRANSACTION;
DELETE FROM iot_things_model
WHERE product_id = 34 AND type = 1 AND create_by = 'codex'
  AND identifier NOT IN ('AssetsCode', 'Class', 'Jdl', 'Kwh', 'Satus', 'C_TimeStamp', 'NowAccount', 'ProductCode', 'LotCode', 'SubLotID', 'SpRmp', 'DeepOffset', 'ToolComp', 'MillingSpeed', 'DepthControl', 'Diameter', 'LengthCompensation1', 'LengthCompensation2', 'LengthCompensation3', 'LengthCompensation4', 'LengthCompensation5', 'LengthCompensation6', 'StartRunTime', 'StopRunTime', 'RunTime', 'UtilizationRate', 'FeedSpeed', 'BackSpeed', 'ToolLife', 'TimeStamp');
SELECT ROW_COUNT() AS removed_unselected_fields;
INSERT INTO iot_things_model
  (model_name, product_id, product_name, tenant_id, tenant_name,
   identifier, type, datatype, specs, is_chart, is_monitor, is_history,
   is_readonly, is_share_perm, model_order, del_flag, create_by, create_time)
SELECT f.model_name, p.product_id, p.product_name, p.tenant_id, p.tenant_name,
       f.identifier, 1, f.datatype,
       CASE WHEN f.datatype = 'string'
         THEN JSON_OBJECT('type', 'string', 'maxLength', 255)
         WHEN f.identifier IN ('Jdl', 'UtilizationRate')
         THEN JSON_OBJECT('type', 'decimal', 'min', 0, 'max', 100, 'step', 0.001, 'unit', f.unit)
         WHEN f.datatype = 'integer'
         THEN JSON_OBJECT('type', 'integer', 'min', -2147483648, 'max', 2147483647, 'step', 1, 'unit', f.unit)
         ELSE JSON_OBJECT('type', 'decimal', 'min', -1000000000, 'max', 1000000000, 'step', 0.001, 'unit', f.unit)
       END,
       IF(f.identifier IN ('Jdl', 'UtilizationRate'), 1, 0),
       0, 0, 1, 0, f.model_order, '0', 'codex', NOW()
FROM iot_product AS p
JOIN JSON_TABLE('
[
  {
    "id": "AssetsCode",
    "name": "设备编号",
    "type": "string",
    "unit": ""
  },
  {
    "id": "Class",
    "name": "班次",
    "type": "string",
    "unit": ""
  },
  {
    "id": "Jdl",
    "name": "稼动率",
    "type": "decimal",
    "unit": "%"
  },
  {
    "id": "Kwh",
    "name": "电量",
    "type": "decimal",
    "unit": "kWh"
  },
  {
    "id": "Satus",
    "name": "机床状态",
    "type": "integer",
    "unit": ""
  },
  {
    "id": "C_TimeStamp",
    "name": "设备采集时间",
    "type": "string",
    "unit": ""
  },
  {
    "id": "NowAccount",
    "name": "工艺_当前账号",
    "type": "string",
    "unit": ""
  },
  {
    "id": "ProductCode",
    "name": "工艺_产品编号",
    "type": "string",
    "unit": ""
  },
  {
    "id": "LotCode",
    "name": "工艺_批次号",
    "type": "string",
    "unit": ""
  },
  {
    "id": "SubLotID",
    "name": "工艺_子批次号",
    "type": "string",
    "unit": ""
  },
  {
    "id": "SpRmp",
    "name": "主轴转速",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "DeepOffset",
    "name": "深度补偿",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "ToolComp",
    "name": "刀具补偿",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "MillingSpeed",
    "name": "锣切速度",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "DepthControl",
    "name": "深度控制",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "Diameter",
    "name": "刀径",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "LengthCompensation1",
    "name": "长度补偿1",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "LengthCompensation2",
    "name": "长度补偿2",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "LengthCompensation3",
    "name": "长度补偿3",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "LengthCompensation4",
    "name": "长度补偿4",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "LengthCompensation5",
    "name": "长度补偿5",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "LengthCompensation6",
    "name": "长度补偿6",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "StartRunTime",
    "name": "开始运行时间",
    "type": "string",
    "unit": ""
  },
  {
    "id": "StopRunTime",
    "name": "停止运行时间",
    "type": "string",
    "unit": ""
  },
  {
    "id": "RunTime",
    "name": "运行时长",
    "type": "string",
    "unit": ""
  },
  {
    "id": "UtilizationRate",
    "name": "工艺利用率",
    "type": "decimal",
    "unit": "%"
  },
  {
    "id": "FeedSpeed",
    "name": "进给速度",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "BackSpeed",
    "name": "回退速度",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "ToolLife",
    "name": "刀具寿命",
    "type": "decimal",
    "unit": ""
  },
  {
    "id": "TimeStamp",
    "name": "工艺参数时间",
    "type": "string",
    "unit": ""
  }
]
', '$[*]' COLUMNS (
  model_order FOR ORDINALITY,
  identifier VARCHAR(32) PATH '$.id',
  model_name VARCHAR(64) PATH '$.name',
  datatype VARCHAR(64) PATH '$.type',
  unit VARCHAR(16) PATH '$.unit'
)) AS f
WHERE p.product_id = 34 AND p.status = 2
  AND NOT EXISTS (
    SELECT 1 FROM iot_things_model AS existing
    WHERE existing.product_id = p.product_id
      AND existing.identifier = f.identifier
      AND existing.del_flag = '0'
  );
SELECT ROW_COUNT() AS inserted_model_fields;
UPDATE iot_things_model
SET is_chart = 1, specs = JSON_SET(specs, '$.min', 0, '$.max', 100)
WHERE product_id = 34 AND type = 1 AND del_flag = '0'
  AND create_by = 'codex' AND identifier IN ('Jdl', 'UtilizationRate');
SELECT ROW_COUNT() AS updated_chart_fields;
SELECT COUNT(*) AS active_model_fields
FROM iot_things_model WHERE product_id = 34 AND del_flag = '0';
COMMIT;
