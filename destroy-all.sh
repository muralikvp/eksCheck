#!/bin/bash

set -euo pipefail

REGION="ap-south-1"
CLUSTER_NAME="my-eks-cluster"

echo "======================================="
echo "Ì∫Ä STARTING FULL AWS CLEANUP"
echo "Region: $REGION"
echo "Cluster: $CLUSTER_NAME"
echo "======================================="

# ---------------------------------------
# Helper: safe execution (no crash)
# ---------------------------------------
run_safe () {
  "$@" || echo "‚ö†Ô∏è Skipped/Failed: $*"
}

# ---------------------------------------
# 1. Delete Kubernetes resources
# ---------------------------------------
echo "Ì∑π Cleaning Kubernetes resources..."

run_safe kubectl delete all --all
run_safe kubectl delete pvc --all
run_safe kubectl delete pv --all
run_safe kubectl delete namespace keda

# ---------------------------------------
# 2. Delete EKS Cluster (BIG COST SAVER)
# ---------------------------------------
echo "Ì¥• Deleting EKS Cluster..."

if eksctl get cluster --region $REGION | grep -q $CLUSTER_NAME; then
  run_safe eksctl delete cluster \
    --name $CLUSTER_NAME \
    --region $REGION
else
  echo "‚ÑπÔ∏è Cluster not found"
fi

# ---------------------------------------
# 3. Delete Load Balancers (ALB/NLB)
# ---------------------------------------
echo "Ìºê Deleting Load Balancers..."

LBS=$(aws elbv2 describe-load-balancers \
  --region $REGION \
  --query "LoadBalancers[].LoadBalancerArn" \
  --output text)

for lb in $LBS; do
  run_safe aws elbv2 delete-load-balancer \
    --load-balancer-arn $lb \
    --region $REGION
done

# ---------------------------------------
# 4. Delete Target Groups
# ---------------------------------------
echo "ÌæØ Deleting Target Groups..."

TGS=$(aws elbv2 describe-target-groups \
  --region $REGION \
  --query "TargetGroups[].TargetGroupArn" \
  --output text)

for tg in $TGS; do
  run_safe aws elbv2 delete-target-group \
    --target-group-arn $tg \
    --region $REGION
done

# ---------------------------------------
# 5. Delete RDS Instances
# ---------------------------------------
echo "Ìª¢Ô∏è Deleting RDS instances..."

DBS=$(aws rds describe-db-instances \
  --region $REGION \
  --query "DBInstances[].DBInstanceIdentifier" \
  --output text)

for db in $DBS; do
  run_safe aws rds delete-db-instance \
    --db-instance-identifier $db \
    --skip-final-snapshot \
    --region $REGION
done

echo "‚è≥ Waiting for RDS deletion..."
for db in $DBS; do
  run_safe aws rds wait db-instance-deleted \
    --db-instance-identifier $db \
    --region $REGION
done

# Delete subnet groups
SUBNETS=$(aws rds describe-db-subnet-groups \
  --region $REGION \
  --query "DBSubnetGroups[].DBSubnetGroupName" \
  --output text)

for subnet in $SUBNETS; do
  run_safe aws rds delete-db-subnet-group \
    --db-subnet-group-name $subnet \
    --region $REGION
done

# ---------------------------------------
# 6. Delete SQS Queues
# ---------------------------------------
echo "Ì≥¨ Deleting SQS queues..."

QUEUES=$(aws sqs list-queues \
  --region $REGION \
  --query "QueueUrls[]" \
  --output text)

for q in $QUEUES; do
  run_safe aws sqs delete-queue \
    --queue-url $q \
    --region $REGION
done

# ---------------------------------------
# 7. Delete IAM Policies (CUSTOM ONLY)
# ---------------------------------------
echo "Ì¥ê Deleting IAM policies..."

POLICIES=$(aws iam list-policies \
  --scope Local \
  --query "Policies[].Arn" \
  --output text)

for policy in $POLICIES; do
  VERSIONS=$(aws iam list-policy-versions \
    --policy-arn $policy \
    --query "Versions[?IsDefaultVersion==\`false\`].VersionId" \
    --output text)

  for v in $VERSIONS; do
    run_safe aws iam delete-policy-version \
      --policy-arn $policy \
      --version-id $v
  done

  run_safe aws iam delete-policy --policy-arn $policy
done

# ---------------------------------------
# 8. Delete EBS Volumes
# ---------------------------------------
echo "Ì≤æ Deleting unused EBS volumes..."

VOLUMES=$(aws ec2 describe-volumes \
  --region $REGION \
  --filters Name=status,Values=available \
  --query "Volumes[].VolumeId" \
  --output text)

for vol in $VOLUMES; do
  run_safe aws ec2 delete-volume \
    --volume-id $vol \
    --region $REGION
done

# ---------------------------------------
# 9. Release Elastic IPs
# ---------------------------------------
echo "Ìºç Releasing Elastic IPs..."

EIPS=$(aws ec2 describe-addresses \
  --region $REGION \
  --query "Addresses[].AllocationId" \
  --output text)

for eip in $EIPS; do
  run_safe aws ec2 release-address \
    --allocation-id $eip \
    --region $REGION
done

# ---------------------------------------
# 10. Delete Security Groups (non-default)
# ---------------------------------------
echo "Ìª°Ô∏è Deleting Security Groups..."

SGS=$(aws ec2 describe-security-groups \
  --region $REGION \
  --query "SecurityGroups[?GroupName!='default'].GroupId" \
  --output text)

for sg in $SGS; do
  run_safe aws ec2 delete-security-group \
    --group-id $sg \
    --region $REGION
done

# ---------------------------------------
# 11. Delete CloudWatch Logs
# ---------------------------------------
echo "Ì≥ú Deleting CloudWatch log groups..."

LOGS=$(aws logs describe-log-groups \
  --region $REGION \
  --query "logGroups[].logGroupName" \
  --output text)

for log in $LOGS; do
  run_safe aws logs delete-log-group \
    --log-group-name "$log" \
    --region $REGION
done

# ---------------------------------------
# DONE
# ---------------------------------------
echo "======================================="
echo "‚úÖ CLEANUP COMPLETE"
echo "======================================="
